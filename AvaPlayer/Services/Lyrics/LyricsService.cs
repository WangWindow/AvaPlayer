using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AvaPlayer.Helpers;
using AvaPlayer.Models;
using AvaPlayer.Services.Cache;
using AvaPlayer.Services.Network;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.Lyrics;

public sealed class LyricsService : ILyricsService
{
    private const string CacheKeyVersion = "lyrics-v2";
    private const string CacheCategory = "lyrics";

    /// <summary>歌词与曲目绑定、几乎不会变:命中后 30 天内不再联网刷新。</summary>
    private static readonly TimeSpan LyricsCacheTtl = TimeSpan.FromDays(30);

    /// <summary>「查过、确实没有歌词」的结论缓存 7 天,避免每次换曲都重打全部 provider。</summary>
    private static readonly TimeSpan MissingLyricsTtl = TimeSpan.FromDays(7);

    private readonly ICacheService _cacheService;
    private readonly INetworkAccessService _networkAccessService;
    private readonly ILogger<LyricsService> _logger;
    private readonly IReadOnlyList<ILyricsProvider> _providers;

    public LyricsService(ICacheService cacheService, ILyricsProviderManager providerManager, INetworkAccessService networkAccessService, ILogger<LyricsService> logger)
    {
        _cacheService = cacheService;
        _networkAccessService = networkAccessService;
        _logger = logger;
        _providers = providerManager.Providers;
    }

    public async Task<IReadOnlyList<LyricLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        // 同目录外挂 .lrc 永远是最高优先级:用户手工整理的数据,不联网、也不写进缓存。
        var sidecarPath = Path.ChangeExtension(track.FilePath, ".lrc");
        if (File.Exists(sidecarPath))
        {
            var sidecarText = await File.ReadAllTextAsync(sidecarPath, cancellationToken);
            return LrcParser.Parse(sidecarText);
        }

        var cacheKey = BuildCacheKey(track);
        LyricsSelection? bestFallback = null;

        var cached = await ReadCachedLyricsAsync(cacheKey, cancellationToken);
        if (cached.Count > 0)
        {
            var cachedSelection = CreateSelection(track, cached);
            if (cachedSelection.Quality.IsAccepted)
            {
                return cached;
            }

            bestFallback = cachedSelection;
        }

        // 墓碑(negative cache)命中时跳过联网:上次查过没有,短期内重打 4 个 provider 也大概率没有。
        if (_networkAccessService.IsEnabled && !await IsKnownMissingAsync(cacheKey, cancellationToken))
        {
            var (winner, raceFallback, attempted) = await RaceProvidersAsync(track, cancellationToken);

            if (winner is { } accepted)
            {
                await _cacheService.SetTextAsync(
                    CacheCategory, cacheKey, FormatCachedLyrics(accepted.Lyrics), LyricsCacheTtl, cancellationToken);
                return accepted.Lyrics;
            }

            if (raceFallback is { } fallback)
            {
                if (bestFallback is null || fallback.Quality.Score > bestFallback.Value.Quality.Score)
                {
                    bestFallback = fallback;
                }
            }
            else if (attempted)
            {
                await _cacheService.MarkMissingAsync(CacheCategory, cacheKey, MissingLyricsTtl, cancellationToken);
            }
        }

        return bestFallback?.Lyrics ?? [];
    }

    /// <summary>
    /// 并发发起所有 provider,第一个「质量被接受」的结果立即返回并取消其余任务。
    /// 为什么是 race 而不是 WhenAll:WhenAll 要等最慢的 provider 结束,换曲延迟 = 所有源之和的量级;
    /// race 让延迟 = 最快达标的源,挂死或超时的源由链接的取消令牌撤掉,不再拖住整条链路。
    /// 全部未达标时,返回其中质量最高的 fallback(与原串行实现语义一致)。
    /// </summary>
    private async Task<(LyricsSelection? Winner, LyricsSelection? BestFallback, bool Attempted)> RaceProvidersAsync(
        Track track,
        CancellationToken cancellationToken)
    {
        // 链接调用方 token:提前返回(Dispose 即取消)或调用方换曲时,撤掉仍在飞的请求。
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var pending = new List<Task<LyricsSelection?>>(_providers.Count);
        foreach (var provider in _providers)
        {
            pending.Add(QueryProviderAsync(provider, track, raceCts.Token));
        }

        var attempted = pending.Count > 0;
        LyricsSelection? bestFallback = null;
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);

            // QueryProviderAsync 内部消化了全部异常,这里 await 不会抛出。
            if (await completed is not { } selection)
            {
                continue;
            }

            if (selection.Quality.IsAccepted)
            {
                return (selection, null, true);
            }

            if (bestFallback is null || selection.Quality.Score > bestFallback.Value.Quality.Score)
            {
                bestFallback = selection;
            }
        }

        // 竞速期间调用方可能已换曲:保持原有语义,把取消异常抛给上层。
        cancellationToken.ThrowIfCancellationRequested();
        return (null, bestFallback, attempted);
    }

    private async Task<LyricsSelection?> QueryProviderAsync(
        ILyricsProvider provider,
        Track track,
        CancellationToken cancellationToken)
    {
        try
        {
            var lyrics = await provider.GetLyricsAsync(track, cancellationToken);
            return lyrics is { Count: > 0 } ? CreateSelection(track, lyrics) : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 竞速取消或调用方换曲:记为 miss,调用方取消由竞速收口处统一抛出。
            return null;
        }
        catch (Exception ex)
        {
            // 单个 provider 失败不能影响其他竞速任务。
            _logger.LogWarning(ex, "[Lyrics:{Provider}] 获取歌词失败: {Message}", provider.Name, ex.Message);
            return null;
        }
    }

    private static string BuildCacheKey(Track track)
    {
        var title = string.IsNullOrWhiteSpace(track.Title)
            ? Path.GetFileNameWithoutExtension(track.FilePath)
            : track.Title;
        var usePathFallback = string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Album);
        var bytes = Encoding.UTF8.GetBytes(
            $"{CacheKeyVersion}|{title}|{track.Artist}|{track.Album}|{Math.Round(Math.Max(0, track.DurationSeconds)).ToString("0", CultureInfo.InvariantCulture)}|{(usePathFallback ? track.FilePath : string.Empty)}");
        return Convert.ToHexString(SHA1.HashData(bytes));
    }

    private async Task<IReadOnlyList<LyricLine>> ReadCachedLyricsAsync(string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            var text = await _cacheService.GetTextAsync(CacheCategory, cacheKey, cancellationToken);
            return text is null ? [] : ParseCachedLyrics(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Lyrics] 读取缓存歌词失败: {Message}", ex.Message);
            return [];
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
            _logger.LogWarning(ex, "[Lyrics] 读取歌词缺失标记失败: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 单趟解析缓存文本:每行 `ticks\tbase64(UTF-8 歌词)`。base64 保证歌词内的换行不破坏行协议;
    /// 写入时已按时间升序,因此读取不再需要 OrderBy。
    /// </summary>
    private static IReadOnlyList<LyricLine> ParseCachedLyrics(string text)
    {
        var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<LyricLine>(rows.Length);
        foreach (var row in rows)
        {
            var tab = row.IndexOf('\t');
            if (tab <= 0 ||
                !long.TryParse(row.AsSpan(0, tab), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                ticks < 0)
            {
                continue;
            }

            lines.Add(new LyricLine
            {
                Time = new TimeSpan(ticks),
                Text = DecodeCachedText(row[(tab + 1)..].TrimEnd('\r'))
            });
        }

        return lines;
    }

    private static string FormatCachedLyrics(IReadOnlyList<LyricLine> lyrics)
    {
        var builder = new StringBuilder();
        foreach (var line in lyrics.OrderBy(static item => item.Time))
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder
                .Append(line.Time.Ticks)
                .Append('\t')
                .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(line.Text)));
        }

        return builder.ToString();
    }

    private static LyricsSelection CreateSelection(Track track, IReadOnlyList<LyricLine> lyrics) =>
        new(lyrics, LyricsQualityEvaluator.Evaluate(track, lyrics));

    private static string DecodeCachedText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return value;
        }
    }

    private readonly record struct LyricsSelection(IReadOnlyList<LyricLine> Lyrics, LyricsQualityEvaluation Quality);
}
