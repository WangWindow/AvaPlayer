using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using AvaPlayer.Models;
using AvaPlayer.Services.Cache;
using AvaPlayer.Services.Network;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.AlbumArt;

public sealed class AlbumArtService : IAlbumArtService
{
    private const int AlbumArtDecodeWidth = 360;

    private readonly ICacheService _cacheService;
    private readonly INetworkAccessService _networkAccessService;
    private readonly ILogger<AlbumArtService> _logger;
    private readonly IReadOnlyList<IAlbumArtProvider> _providers;

    public AlbumArtService(
        ICacheService cacheService,
        IAlbumArtProviderManager providerManager,
        INetworkAccessService networkAccessService,
        ILogger<AlbumArtService> logger)
    {
        _cacheService = cacheService;
        _networkAccessService = networkAccessService;
        _logger = logger;
        _providers = providerManager.Providers;
    }

    public async Task<Bitmap?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default)
    {
        var cachePath = _cacheService.GetFilePath("album-art", $"{BuildCacheKey(track)}.img");
        if (File.Exists(cachePath))
        {
            return LoadBitmapFromFile(cachePath);
        }

        foreach (var provider in _providers)
        {
            if (provider.RequiresNetwork && !_networkAccessService.IsEnabled)
            {
                continue;
            }

            try
            {
                var bytes = await provider.GetAlbumArtAsync(track, cancellationToken);
                if (bytes is not { Length: > 0 })
                {
                    continue;
                }

                await File.WriteAllBytesAsync(cachePath, bytes, cancellationToken);
                var bitmap = CreateBitmap(bytes);
                if (bitmap is not null)
                {
                    return bitmap;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AlbumArt:{Provider}] 获取封面失败: {Message}", provider.Name, ex.Message);
            }
        }

        return null;
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
