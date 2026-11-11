using System.Net;
using AvaPlayer.Models;

namespace AvaPlayer.Services.AlbumArt;

public sealed class LrcApiAlbumArtProvider(IHttpClientFactory httpClientFactory) : IAlbumArtProvider
{
    private const string Endpoint = "https://api.lrc.cx/cover";

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

        var client = httpClientFactory.CreateClient();
        using var response = await client.GetAsync(
            $"{Endpoint}?{string.Join("&", query)}",
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            // 2xx 却不是图片通常意味着上游错误页/限流页,不是「确定没有封面」;
            // 抛出让 service 避免写长期墓碑。
            throw new HttpRequestException(
                $"Album art provider returned unexpected content type '{contentType}'.");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }
}
