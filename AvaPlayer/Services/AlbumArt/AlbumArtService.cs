using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using AvaPlayer.Models;
using AvaPlayer.Services.Cache;
using AvaPlayer.Services.Network;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.AlbumArt;

public sealed class AlbumArtService(
    ICacheService cacheService,
    IAlbumArtProviderManager providerManager,
    INetworkAccessService networkAccessService,
    ILogger<AlbumArtService> logger) : IAlbumArtService
{
    private const int AlbumArtDecodeWidth = 360;
    private const string CacheKeyVersion = "album-art-v2";
    private const string CacheCategory = "album-art";

    /// <summary>封面与专辑绑定、极少变化:命中后 90 天内不再联网刷新。</summary>
    private static readonly TimeSpan AlbumArtCacheTtl = TimeSpan.FromDays(90);

    /// <summary>「查过、确实没有封面」的结论缓存 14 天,避免每次换曲都重打网络 provider。</summary>
    private static readonly TimeSpan MissingAlbumArtTtl = TimeSpan.FromDays(14);

    private readonly IReadOnlyList<IAlbumArtProvider> _providers = providerManager.Providers;

    public async Task<Bitmap?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cacheKey = BuildCacheKey(track);

        // Read embedded artwork before the download cache so tag edits are visible immediately.
        foreach (var provider in _providers)
        {
            if (provider.RequiresNetwork)
            {
                continue;
            }

            var embeddedBytes = await QueryProviderAsync(provider, track, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (embeddedBytes.Bytes is { Length: > 0 } bytes && CreateBitmap(bytes) is { } embeddedBitmap)
            {
                return embeddedBitmap;
            }
        }

        // 墓碑(negative cache)命中时跳过缓存与联网:上次查过没有,短期内重打网络源大概率也没有。
        // 必须先查墓碑再读缓存:CacheService.GetBytesAsync 命中墓碑会删除它。
        if (await IsKnownMissingAsync(cacheKey, cancellationToken))
        {
            return null;
        }

        // 缓存命中但解码失败视为脏数据,继续走 provider 并覆盖写入。
        var cachedBytes = await ReadCachedAlbumArtAsync(cacheKey, cancellationToken);
        if (cachedBytes is { Length: > 0 } && CreateBitmap(cachedBytes) is { } cachedBitmap)
        {
            return cachedBitmap;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (networkAccessService.IsEnabled)
        {
            var (winner, attempted, hadTransientFailure) = await RaceProvidersAsync(track, cancellationToken);
            if (winner is { } result)
            {
                await TryCacheAlbumArtAsync(cacheKey, result.Bytes, cancellationToken);
                return result.Bitmap;
            }

            // 只有所有网络源都明确回答「没有」时才写墓碑;任一瞬时故障都不能压制后续重试。
            if (attempted && !hadTransientFailure)
            {
                await TryMarkMissingAsync(cacheKey, cancellationToken);
            }
        }

        return null;
    }

    /// <summary>
    /// 并发发起所有需要联网的 provider,第一个能解码成位图的结果立即返回并取消其余任务。
    /// 为什么是 race 而不是 WhenAll:WhenAll 要等最慢的 provider 结束,换曲延迟 = 所有源之和的量级;
    /// race 让延迟 = 最快响应的源,挂死或超时的源由链接的取消令牌撤掉,不再拖住整条链路。
    /// </summary>
    private async Task<(AlbumArtResult? Winner, bool Attempted, bool HadTransientFailure)> RaceProvidersAsync(
        Track track,
        CancellationToken cancellationToken)
    {
        // Cancel the remaining requests explicitly when a winner is found.
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var pending = new List<Task<ProviderOutcome>>(_providers.Count);
        foreach (var provider in _providers)
        {
            if (provider.RequiresNetwork)
            {
                pending.Add(QueryProviderAsync(provider, track, raceCts.Token));
            }
        }

        var attempted = pending.Count > 0;
        if (!attempted)
        {
            return (null, false, false);
        }

        var hadTransientFailure = false;

        // 调用方取消不应依赖任何 provider 是否及时响应:单独用一个信号任务参与 WhenAny。
        var cancellationSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            cancellationSignal);

        try
        {
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending.Append(cancellationSignal.Task));
                if (ReferenceEquals(completed, cancellationSignal.Task))
                {
                    break;
                }

                var providerTask = (Task<ProviderOutcome>)completed;
                pending.Remove(providerTask);

                // QueryProviderAsync 内部消化了全部异常,这里 await 不会抛出。
                var outcome = await providerTask;
                if (outcome.TransientFailure)
                {
                    hadTransientFailure = true;
                    continue;
                }

                if (outcome.Bytes is not { Length: > 0 } bytes)
                {
                    continue;
                }

                if (CreateBitmap(bytes) is { } bitmap)
                {
                    raceCts.Cancel();
                    if (cancellationToken.IsCancellationRequested)
                    {
                        bitmap.Dispose();
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    return (new AlbumArtResult(bytes, bitmap), attempted, hadTransientFailure);
                }

                // 成功响应却解码失败,说明数据源异常而不是「确定没有封面」;避免写墓碑。
                hadTransientFailure = true;
            }
        }
        finally
        {
            raceCts.Cancel();
            foreach (var task in pending)
            {
                ObserveFault(task);
            }
        }

        // 竞速期间调用方可能已换曲:保持原有语义,把取消异常抛给上层。
        cancellationToken.ThrowIfCancellationRequested();
        return (null, attempted, hadTransientFailure);
    }

    private async Task<ProviderOutcome> QueryProviderAsync(
        IAlbumArtProvider provider,
        Track track,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await provider.GetAlbumArtAsync(track, cancellationToken);
            return new ProviderOutcome(bytes is { Length: > 0 } ? bytes : null, TransientFailure: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 竞速取消或调用方换曲:记为 miss,调用方取消由竞速收口处统一抛出。
            return new ProviderOutcome(null, TransientFailure: false);
        }
        catch (Exception ex)
        {
            // 单个 provider 失败不能影响其他竞速任务,但要让 service 知道这次结论不可信。
            logger.LogWarning(ex, "[AlbumArt:{Provider}] 获取封面失败: {Message}", provider.Name, ex.Message);
            return new ProviderOutcome(null, TransientFailure: true);
        }
    }

    private async Task TryCacheAlbumArtAsync(string cacheKey, byte[] bytes, CancellationToken cancellationToken)
    {
        try
        {
            await cacheService.SetBytesAsync(
                CacheCategory, cacheKey, bytes, AlbumArtCacheTtl, cancellationToken);
        }
        catch (Exception ex)
        {
            // 缓存失败不应丢掉已经成功获取并解码的封面。
            logger.LogWarning(ex, "[AlbumArt] 写入缓存封面失败: {Message}", ex.Message);
        }
    }

    private async Task TryMarkMissingAsync(string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            await cacheService.MarkMissingAsync(CacheCategory, cacheKey, MissingAlbumArtTtl, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AlbumArt] 写入封面缺失标记失败: {Message}", ex.Message);
        }
    }

    private static void ObserveFault(Task task)
    {
        // 竞速结束后不等待慢 provider,但要显式观察潜在异常,避免留下未观察的 Task 故障。
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<byte[]?> ReadCachedAlbumArtAsync(string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            return await cacheService.GetBytesAsync(CacheCategory, cacheKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AlbumArt] 读取缓存封面失败: {Message}", ex.Message);
            return null;
        }
    }

    private async Task<bool> IsKnownMissingAsync(string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            return await cacheService.IsKnownMissingAsync(CacheCategory, cacheKey, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[AlbumArt] 读取封面缺失标记失败: {Message}", ex.Message);
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
            logger.LogWarning(ex, "[AlbumArt] 解码封面失败: {Message}", ex.Message);
            return null;
        }
    }

    private static string BuildCacheKey(Track track)
    {
        // DisplayArtist/DisplayAlbum 对未打标签的曲目会退化成 "未知艺术家"/"未知专辑",
        // 仅用这三个展示字段会让同名的未标记文件互相命中错误的封面。只有在缺少可用
        // 元数据时才把文件路径纳入 key,标签完整的专辑仍可共享缓存。
        var usePathFallback =
            string.IsNullOrWhiteSpace(track.Artist) ||
            string.IsNullOrWhiteSpace(track.Album) ||
            string.IsNullOrWhiteSpace(track.Title);
        var bytes = Encoding.UTF8.GetBytes(
            $"{CacheKeyVersion}|{track.DisplayArtist}|{track.DisplayAlbum}|{track.DisplayTitle}|{(usePathFallback ? track.FilePath : string.Empty)}");
        return Convert.ToHexString(SHA1.HashData(bytes));
    }

    private readonly record struct AlbumArtResult(byte[] Bytes, Bitmap Bitmap);

    private readonly record struct ProviderOutcome(byte[]? Bytes, bool TransientFailure);
}
