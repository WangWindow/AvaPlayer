using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using AvaPlayer.Models;
using AvaPlayer.Services.Cache;
using AvaPlayer.Services.Network;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.AlbumArt;

public sealed class AlbumArtService : IAlbumArtService
{
    private const int AlbumArtDecodeWidth = 360;

    private readonly ICacheService _cacheService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly INetworkAccessService _networkAccessService;
    private readonly ILogger<AlbumArtService> _logger;

    public AlbumArtService(ICacheService cacheService, IHttpClientFactory httpClientFactory, INetworkAccessService networkAccessService, ILogger<AlbumArtService> logger)
    {
        _cacheService = cacheService;
        _httpClientFactory = httpClientFactory;
        _networkAccessService = networkAccessService;
        _logger = logger;
    }

    public async Task<Bitmap?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default)
    {
        var cachePath = _cacheService.GetFilePath("album-art", $"{BuildCacheKey(track)}.img");

        if (File.Exists(cachePath))
        {
            return LoadBitmapFromFile(cachePath);
        }

        var embedded = TryReadEmbeddedCover(track.FilePath);
        if (embedded is { Length: > 0 })
        {
            await File.WriteAllBytesAsync(cachePath, embedded, cancellationToken);
            return CreateBitmap(embedded);
        }

        if (_networkAccessService.IsEnabled)
        {
            var onlineCover = await TryFetchOnlineCoverAsync(track, cancellationToken);
            if (onlineCover is { Length: > 0 })
            {
                await File.WriteAllBytesAsync(cachePath, onlineCover, cancellationToken);
                return CreateBitmap(onlineCover);
            }
        }

        return null;
    }

    private byte[]? TryReadEmbeddedCover(string filePath)
    {
        try
        {
            using var tagFile = TagLib.File.Create(filePath);
            return tagFile.Tag.Pictures.FirstOrDefault()?.Data.Data;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AlbumArt] 读取内嵌封面失败: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<byte[]?> TryFetchOnlineCoverAsync(Track track, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        var term = Uri.EscapeDataString($"{track.DisplayArtist} {track.DisplayAlbum} {track.DisplayTitle}");
        using var response = await client.GetAsync($"https://itunes.apple.com/search?term={term}&entity=song&limit=1", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

        if (!document.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
        {
            return null;
        }

        if (!results[0].TryGetProperty("artworkUrl100", out var artworkUrlElement))
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

    private Bitmap? LoadBitmapFromFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, AlbumArtDecodeWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AlbumArt] 读取缓存封面失败: {Message}", ex.Message);
            return null;
        }
    }

    private Bitmap? CreateBitmap(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(stream, AlbumArtDecodeWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AlbumArt] 解码封面失败: {Message}", ex.Message);
            return null;
        }
    }

    private static string BuildCacheKey(Track track)
    {
        var bytes = Encoding.UTF8.GetBytes($"{track.DisplayArtist}|{track.DisplayAlbum}|{track.DisplayTitle}");
        return Convert.ToHexString(SHA1.HashData(bytes));
    }
}
