using AvaPlayer.Models;

namespace AvaPlayer.Services.AlbumArt;

public sealed class LrcApiAlbumArtProvider : IAlbumArtProvider
{
    private const string Endpoint = "https://api.lrc.cx/cover";
    private readonly IHttpClientFactory _httpClientFactory;

    public LrcApiAlbumArtProvider(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "LrcAPI";
    public bool RequiresNetwork => true;

    public async Task<byte[]?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default)
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

        var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(
            $"{Endpoint}?{string.Join("&", query)}",
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}
