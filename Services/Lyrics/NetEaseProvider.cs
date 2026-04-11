using System.Net.Http.Headers;
using System.Text.Json;
using AvaPlayer.Helpers;
using AvaPlayer.Models;

namespace AvaPlayer.Services.Lyrics;

public sealed class NetEaseProvider : ILyricsProvider
{
    private readonly IHttpClientFactory _httpClientFactory;

    public NetEaseProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "NetEase";

    public async Task<IReadOnlyList<LyricLine>?> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        var songId = await SearchSongIdAsync(track, cancellationToken);
        if (songId is null)
        {
            return null;
        }

        var client = _httpClientFactory.CreateClient();
        using var lyricRequest = CreateRequest($"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1");
        using var lyricResponse = await client.SendAsync(lyricRequest, cancellationToken);
        if (!lyricResponse.IsSuccessStatusCode)
        {
            return null;
        }

        await using var lyricStream = await lyricResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var lyricDocument = await JsonDocument.ParseAsync(lyricStream, cancellationToken: cancellationToken);

        if (!lyricDocument.RootElement.TryGetProperty("lrc", out var lrcElement) ||
            !lrcElement.TryGetProperty("lyric", out var lyricElement))
        {
            return null;
        }

        var lyricText = lyricElement.GetString();
        return string.IsNullOrWhiteSpace(lyricText)
            ? null
            : LrcParser.Parse(lyricText);
    }

    private async Task<long?> SearchSongIdAsync(Track track, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"{track.DisplayTitle} {track.DisplayArtist}");
        var client = _httpClientFactory.CreateClient();
        using var request = CreateRequest($"https://music.163.com/api/cloudsearch/pc?s={query}&type=1&limit=1");
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("result", out var resultElement) ||
            !resultElement.TryGetProperty("songs", out var songsElement) ||
            songsElement.ValueKind != JsonValueKind.Array ||
            songsElement.GetArrayLength() == 0)
        {
            return null;
        }

        return songsElement[0].TryGetProperty("id", out var idElement)
            ? idElement.GetInt64()
            : null;
    }

    private static HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://music.163.com/");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
        return request;
    }
}
