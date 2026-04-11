using System.Text.Json;
using AvaPlayer.Helpers;
using AvaPlayer.Models;

namespace AvaPlayer.Services.Lyrics;

public sealed class LrcLibProvider : ILyricsProvider
{
    private readonly IHttpClientFactory _httpClientFactory;

    public LrcLibProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "LRCLIB";

    public async Task<IReadOnlyList<LyricLine>?> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        var query = new List<string>
        {
            $"track_name={Uri.EscapeDataString(track.DisplayTitle)}",
            $"artist_name={Uri.EscapeDataString(track.DisplayArtist)}"
        };

        if (!string.IsNullOrWhiteSpace(track.Album))
        {
            query.Add($"album_name={Uri.EscapeDataString(track.DisplayAlbum)}");
        }

        if (track.DurationSeconds > 1)
        {
            query.Add($"duration={(int)Math.Round(track.DurationSeconds)}");
        }

        var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync($"https://lrclib.net/api/get?{string.Join("&", query)}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (document.RootElement.TryGetProperty("syncedLyrics", out var syncedElement))
        {
            var syncedLyrics = syncedElement.GetString();
            if (!string.IsNullOrWhiteSpace(syncedLyrics))
            {
                return LrcParser.Parse(syncedLyrics);
            }
        }

        if (document.RootElement.TryGetProperty("plainLyrics", out var plainElement))
        {
            var plainLyrics = plainElement.GetString();
            if (!string.IsNullOrWhiteSpace(plainLyrics))
            {
                return BuildEstimatedLyrics(plainLyrics, track.DurationSeconds);
            }
        }

        return null;
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
