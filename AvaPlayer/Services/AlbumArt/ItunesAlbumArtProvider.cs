using System.Text.Json;
using AvaPlayer.Models;

namespace AvaPlayer.Services.AlbumArt;

public sealed class ItunesAlbumArtProvider : IAlbumArtProvider
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ItunesAlbumArtProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "iTunes";
    public bool RequiresNetwork => true;

    public async Task<byte[]?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient();
        var term = Uri.EscapeDataString($"{track.DisplayArtist} {track.DisplayAlbum} {track.DisplayTitle}");
        using var response = await client.GetAsync(
            $"https://itunes.apple.com/search?term={term}&entity=song&limit=1",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("results", out var results)
            || results.GetArrayLength() == 0
            || !results[0].TryGetProperty("artworkUrl100", out var artworkUrlElement))
        {
            return null;
        }

        var artworkUrl = artworkUrlElement.GetString();
        if (string.IsNullOrWhiteSpace(artworkUrl))
        {
            return null;
        }

        artworkUrl = artworkUrl
            .Replace("100x100bb", "360x360bb", StringComparison.OrdinalIgnoreCase)
            .Replace("100x100", "360x360", StringComparison.OrdinalIgnoreCase);

        return await client.GetByteArrayAsync(artworkUrl, cancellationToken);
    }
}
