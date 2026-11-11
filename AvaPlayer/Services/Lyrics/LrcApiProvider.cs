using System.Net;
using AvaPlayer.Helpers;
using AvaPlayer.Models;

namespace AvaPlayer.Services.Lyrics;

/// <summary>
/// LrcAPI's legacy single-result endpoint. It returns the matched song as
/// plain LRC text, which can be parsed by the same parser as local .lrc files.
/// </summary>
public sealed class LrcApiProvider(IHttpClientFactory httpClientFactory) : ILyricsProvider
{
    private const string Endpoint = "https://api.lrc.cx/lyrics";

    public string Name => "LrcAPI";

    public async Task<IReadOnlyList<LyricLine>?> GetLyricsAsync(
        Track track,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>
        {
            $"title={Uri.EscapeDataString(track.DisplayTitle)}",
            $"artist={Uri.EscapeDataString(track.DisplayArtist)}"
        };

        if (!string.IsNullOrWhiteSpace(track.Album))
        {
            query.Add($"album={Uri.EscapeDataString(track.DisplayAlbum)}");
        }

        var client = httpClientFactory.CreateClient();
        using var response = await client.GetAsync(
            $"{Endpoint}?{string.Join("&", query)}",
            cancellationToken);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var lrcText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(lrcText))
        {
            return null;
        }

        var lyrics = LrcParser.Parse(lrcText);
        return lyrics.Count > 0 ? lyrics : null;
    }
}
