using System.Text.Json;
using AvaPlayer.Models;

namespace AvaPlayer.Services.Lyrics;

public sealed class LyricsOvhProvider : ILyricsProvider
{
    private readonly IHttpClientFactory _httpClientFactory;

    public LyricsOvhProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "Lyrics.ovh";

    public async Task<IReadOnlyList<LyricLine>?> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        var artist = Uri.EscapeDataString(track.DisplayArtist);
        var title = Uri.EscapeDataString(track.DisplayTitle);
        var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync($"https://api.lyrics.ovh/v1/{artist}/{title}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("lyrics", out var lyricsElement))
        {
            return null;
        }

        var lyrics = lyricsElement.GetString();
        return string.IsNullOrWhiteSpace(lyrics)
            ? null
            : BuildEstimatedLyrics(lyrics, track.DurationSeconds);
    }

    private static IReadOnlyList<LyricLine> BuildEstimatedLyrics(string text, double durationSeconds)
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
                Time = TimeSpan.FromSeconds(index * step),
                Text = line
            })
            .ToArray();
    }
}
