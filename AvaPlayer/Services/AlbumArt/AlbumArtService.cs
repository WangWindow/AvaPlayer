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
    private const string CacheCategory = "album-art";

    /// <summary>封面与专辑绑定、极少变化:命中后 90 天内不再联网刷新。</summary>
    private static readonly TimeSpan AlbumArtCacheTtl = TimeSpan.FromDays(90);

    /// <summary>「查过、确实没有封面」的结论缓存 14 天,避免每次换曲都重打网络 provider。</summary>
    private static readonly TimeSpan MissingAlbumArtTtl = TimeSpan.FromDays(14);

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
        var cacheKey = BuildCacheKey(track);

        // 缓存命中但解码失败视为脏数据,继续走 provider 并覆盖写入。
        var cachedBytes = await ReadCachedAlbumArtAsync(cacheKey, cancellationToken);
        if (cachedBytes is { Length: > 0 } && CreateBitmap(cachedBytes) is { } cachedBitmap)
        {
            return cachedBitmap;
        }

        // 离线 provider(Embedded)先同步尝试:不占网络并发,命中直接落缓存返回。
        foreach (var provider in _providers)
        {
            if (provider.RequiresNetwork)
            {
                continue;
            }

            if (await QueryProviderAsync(provider, track, cancellationToken) is { Length: > 0 } embeddedBytes &&
                CreateBitmap(embeddedBytes) is { } embeddedBitmap)
            {
                await _cacheService.SetBytesAsync(
                    CacheCategory, cacheKey, embeddedBytes, AlbumArtCacheTtl, cancellationToken);
                return embeddedBitmap;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 墓碑(negative cache)命中时跳过联网:上次查过没有,短期内重打网络源大概率也没有。
        if (_networkAccessService.IsEnabled && !await IsKnownMissingAsync(cacheKey, cancellationToken))
        {
            var (winner, attempted) = await RaceProvidersAsync(track, cancellationToken);
            if (winner is { } result)
            {
                await _cacheService.SetBytesAsync(
                    CacheCategory, cacheKey, result.Bytes, AlbumArtCacheTtl, cancellationToken);
                return result.Bitmap;
            }

            if (attempted)
            {
                await _cacheService.MarkMissingAsync(CacheCategory, cacheKey, MissingAlbumArtTtl, cancellationToken);
            }
        }

        return null;
    }

    /// <summary>
    /// 并发发起所有需要联网的 provider,第一个能解码成位图的结果立即返回并取消其余任务。
    /// 为什么是 race 而不是 WhenAll:WhenAll 要等最慢的 provider 结束,换曲延迟 = 所有源之和的量级;
    /// race 让延迟 = 最快响应的源,挂死或超时的源由链接的取消令牌撤掉,不再拖住整条链路。
    /// </summary>
    private async Task<(AlbumArtResult? Winner, bool Attempted)> RaceProvidersAsync(
        Track track,
        CancellationToken cancellationToken)
    {
        // 链接调用方 token:提前返回(Dispose 即取消)或调用方换曲时,撤掉仍在飞的请求。
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var pending = new List<Task<byte[]?>>(_providers.Count);
        foreach (var provider in _providers)
        {
            if (provider.RequiresNetwork)
            {
                pending.Add(QueryProviderAsync(provider, track, raceCts.Token));
            }
        }

        var attempted = pending.Count > 0;
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);

            // QueryProviderAsync 内部消化了全部异常,这里 await 不会抛出。
            if (await completed is not { Length: > 0 } bytes)
            {
                continue;
            }

            if (CreateBitmap(bytes) is { } bitmap)
            {
                return (new AlbumArtResult(bytes, bitmap), attempted);
            }
        }

        // 竞速期间调用方可能已换曲:保持原有语义,把取消异常抛给上层。
        cancellationToken.ThrowIfCancellationRequested();
        return (null, attempted);
    }

    private async Task<byte[]?> QueryProviderAsync(
        IAlbumArtProvider provider,
        Track track,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await provider.GetAlbumArtAsync(track, cancellationToken);
            return bytes is { Length: > 0 } ? bytes : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 竞速取消或调用方换曲:记为 miss,调用方取消由竞速收口处统一抛出。
            return null;
        }
        catch (Exception ex)
        {
            // 单个 provider 失败不能影响其他竞速任务。
            _logger.LogWarning(ex, "[AlbumArt:{Provider}] 获取封面失败: {Message}", provider.Name, ex.Message);
            return null;
        }
    }

    private async Task<byte[]?> ReadCachedAlbumArtAsync(string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            return await _cacheService.GetBytesAsync(CacheCategory, cacheKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AlbumArt] 读取缓存封面失败: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<bool> IsKnownMissingAsync(string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            return await _cacheService.IsKnownMissingAsync(CacheCategory, cacheKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AlbumArt] 读取封面缺失标记失败: {Message}", ex.Message);
            return false;
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

    private readonly record struct AlbumArtResult(byte[] Bytes, Bitmap Bitmap);
}
