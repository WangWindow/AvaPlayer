using System.Net;
using System.Text.Json;
using AvaPlayer.Models;

namespace AvaPlayer.Services.Lyrics;

public sealed class LyricsOvhProvider(IHttpClientFactory httpClientFactory) : ILyricsProvider
{
    public string Name => "Lyrics.ovh";

    public async Task<IReadOnlyList<LyricLine>?> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        var artist = Uri.EscapeDataString(track.DisplayArtist);
        var title = Uri.EscapeDataString(track.DisplayTitle);
        var client = httpClientFactory.CreateClient();
        using var response = await client.GetAsync($"https://api.lyrics.ovh/v1/{artist}/{title}", cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("lyrics", out var lyricsElement))
        {
            return null;
        }

        var lyrics = lyricsElement.GetString();
        return string.IsNullOrWhiteSpace(lyrics)
            ? null
            : LrcLibProviderHelpers.BuildEstimatedLyrics(lyrics, track.DurationSeconds);
    }
}
