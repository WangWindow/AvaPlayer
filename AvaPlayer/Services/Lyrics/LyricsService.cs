using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AvaPlayer.Helpers;
using AvaPlayer.Models;
using AvaPlayer.Services.Cache;
using AvaPlayer.Services.Network;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.Lyrics;

public sealed class LyricsService(ICacheService cacheService, ILyricsProviderManager providerManager, INetworkAccessService networkAccessService, ILogger<LyricsService> logger) : ILyricsService
{
    private const string CacheKeyVersion = "lyrics-v3";
    private const string CacheCategory = "lyrics";

    /// <summary>歌词与曲目绑定、几乎不会变:命中后 30 天内不再联网刷新。</summary>
    private static readonly TimeSpan LyricsCacheTtl = TimeSpan.FromDays(30);

    /// <summary>
    /// 纯文本歌词的时间轴是估算出来的(不可拖动)。保留较短 TTL,以便日后有联网机会时
    /// 换用真正的逐行同步歌词。
    /// </summary>
    private static readonly TimeSpan EstimatedLyricsCacheTtl = TimeSpan.FromDays(7);

    /// <summary>「查过、确实没有歌词」的结论缓存 7 天,避免每次换曲都重打全部 provider。</summary>
    private static readonly TimeSpan MissingLyricsTtl = TimeSpan.FromDays(7);
    private readonly IReadOnlyList<ILyricsProvider> _providers = providerManager.Providers;

    public async Task<IReadOnlyList<LyricLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localLyrics = await ReadLocalLyricsAsync(track, cancellationToken);
        if (localLyrics.Count > 0)
        {
            return localLyrics;
        }

        var cacheKey = BuildCacheKey(track);

        // 必须先查墓碑再看缓存值:CacheService.GetTextAsync 命中墓碑会把它当作 miss 删除,
        // 如果先读缓存,墓碑就永远不会生效。墓碑存在时不可能同时有有效的缓存值。
        if (await IsKnownMissingAsync(cacheKey, cancellationToken))
        {
            return [];
        }

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
        if (networkAccessService.IsEnabled)
        {
            var (winner, raceFallback, attempted, hadTransientFailure) =
                await RaceProvidersAsync(track, cancellationToken);

            LyricsSelection? fromNetwork = winner ?? raceFallback;
            if (fromNetwork is { } candidate && IsBetterFallback(candidate, bestFallback))
            {
                bestFallback = candidate;
            }

            if (winner is { } accepted)
            {
                await TryCacheLyricsAsync(cacheKey, accepted, cancellationToken);
                return accepted.Lyrics;
            }

            if (fromNetwork is { } acceptedFallback &&
                acceptedFallback.Quality.IsAccepted &&
                bestFallback is { } selected &&
                ReferenceEquals(selected.Lyrics, acceptedFallback.Lyrics))
            {
                // 纯文本/估算歌词也缓存,避免每次换曲都重新联网;但 TTL 更短,以便日后升级为同步歌词。
                await TryCacheLyricsAsync(cacheKey, acceptedFallback, cancellationToken);
            }
            else if (raceFallback is null && attempted && !hadTransientFailure)
            {
                // 只有所有 provider 都明确回答「没有」时才写墓碑。任何一个网络/解析异常都意味着
                // 这次结论不可靠,不能把后续重试压制 7 天。
                await TryMarkMissingAsync(cacheKey, cancellationToken);
            }
        }

        return bestFallback?.Lyrics ?? [];
    }

    private static bool IsBetterFallback(LyricsSelection candidate, LyricsSelection? current)
    {
        if (current is not { } existing)
        {
            return true;
        }

        // 先比较「质量是否达标」:被截断的同步歌词不应压过一首合理的纯文本歌词。
        if (candidate.Quality.IsAccepted != existing.Quality.IsAccepted)
        {
            return candidate.Quality.IsAccepted;
        }

        // 在都达标(或都不达标)时,真正的同步歌词优于把纯文本按行数平均分配的伪时间轴。
        if (candidate.Quality.HasTimedLines != existing.Quality.HasTimedLines)
        {
            return candidate.Quality.HasTimedLines;
        }

        return candidate.Quality.Score > existing.Quality.Score;
    }

    /// <summary>
    /// 并发发起所有 provider,第一个「质量被接受」的结果立即返回并取消其余任务。
    /// 为什么是 race 而不是 WhenAll:WhenAll 要等最慢的 provider 结束,换曲延迟 = 所有源之和的量级;
    /// race 让延迟 = 最快达标的源,挂死或超时的源由链接的取消令牌撤掉,不再拖住整条链路。
    /// 全部未达标时,返回其中质量最高的 fallback(与原串行实现语义一致)。
    /// </summary>
    private async Task<(LyricsSelection? Winner, LyricsSelection? BestFallback, bool Attempted, bool HadTransientFailure)> RaceProvidersAsync(
        Track track,
        CancellationToken cancellationToken)
    {
        // Cancel the remaining requests explicitly when a winner is found.
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var pending = new List<Task<ProviderOutcome>>(_providers.Count);
        foreach (var provider in _providers)
        {
            pending.Add(QueryProviderAsync(provider, track, raceCts.Token));
        }

        var attempted = pending.Count > 0;
        if (!attempted)
        {
            return (null, null, false, false);
        }

        var hadTransientFailure = false;
        LyricsSelection? bestFallback = null;

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

                if (outcome.Selection is not { } selection)
                {
                    continue;
                }

                // 只有真正的同步歌词才值得提前结束竞速;估算出来的纯文本继续等待其他 provider,
                // 否则快速返回的纯文本会永远压住稍慢但质量更高的同步歌词。
                if (selection.Quality.IsAccepted && selection.Quality.HasTimedLines)
                {
                    raceCts.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                    return (selection, null, true, hadTransientFailure);
                }

                if (IsBetterFallback(selection, bestFallback))
                {
                    bestFallback = selection;
                }
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
        return (null, bestFallback, attempted, hadTransientFailure);
    }

    private async Task<ProviderOutcome> QueryProviderAsync(
        ILyricsProvider provider,
        Track track,
        CancellationToken cancellationToken)
    {
        try
        {
            var lyrics = await provider.GetLyricsAsync(track, cancellationToken);
            return new ProviderOutcome(
                lyrics is { Count: > 0 } ? CreateSelection(track, lyrics) : null,
                TransientFailure: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 竞速取消或调用方换曲:记为 miss,调用方取消由竞速收口处统一抛出。
            return new ProviderOutcome(null, TransientFailure: false);
        }
        catch (Exception ex)
        {
            // 单个 provider 失败不能影响其他竞速任务,但必须让 service 知道「这次结果不可信」,
            // 以免网络故障被写成长期墓碑。
            logger.LogWarning(ex, "[Lyrics:{Provider}] 获取歌词失败: {Message}", provider.Name, ex.Message);
            return new ProviderOutcome(null, TransientFailure: true);
        }
    }

    private async Task TryCacheLyricsAsync(string cacheKey, LyricsSelection selection, CancellationToken cancellationToken)
    {
        var ttl = selection.Quality.HasTimedLines ? LyricsCacheTtl : EstimatedLyricsCacheTtl;
        try
        {
            await cacheService.SetTextAsync(
                CacheCategory, cacheKey, FormatCachedLyrics(selection.Lyrics), ttl, cancellationToken);
        }
        catch (Exception ex)
        {
            // 缓存失败不应丢掉已经成功获取的歌词。
            logger.LogWarning(ex, "[Lyrics] 写入缓存歌词失败: {Message}", ex.Message);
        }
    }

    private async Task TryMarkMissingAsync(string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            await cacheService.MarkMissingAsync(CacheCategory, cacheKey, MissingLyricsTtl, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Lyrics] 写入歌词缺失标记失败: {Message}", ex.Message);
        }
    }

    private static void ObserveFault(Task task)
    {
        // 竞速结束后不等待慢 provider(否则赢家返回又要被拖住),但要显式观察潜在异常,
        // 避免留下未观察的 Task 故障。
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task<IReadOnlyList<LyricLine>> ReadLocalLyricsAsync(Track track, CancellationToken cancellationToken)
    {
        try
        {
            var embedded = await Task.Run(() =>
            {
                using var file = TagLib.File.Create(track.FilePath);
                return file.Tag.Lyrics;
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(embedded))
            {
                var lines = LrcParser.ParseLocal(embedded);
                if (lines.Count > 0) return lines;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(ex, "[Lyrics] Cannot read embedded lyrics: {Path}", track.FilePath);
            }
        }

        try
        {
            var sidecarPath = Path.ChangeExtension(track.FilePath, ".lrc");
            if (File.Exists(sidecarPath))
                return LrcParser.ParseLocal(await File.ReadAllTextAsync(sidecarPath, cancellationToken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "[Lyrics] Cannot read sidecar: {Path}", track.FilePath);
        }
        return [];
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
            var text = await cacheService.GetTextAsync(CacheCategory, cacheKey, cancellationToken);
            return text is null ? [] : ParseCachedLyrics(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[Lyrics] 读取缓存歌词失败: {Message}", ex.Message);
            return [];
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
            logger.LogWarning(ex, "[Lyrics] 读取歌词缺失标记失败: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 单趟解析缓存文本:每行 `ticks\t是否同步(0/1)\tbase64(UTF-8 歌词)`。
    /// base64 保证歌词内的换行不破坏行协议;写入时已按时间升序,因此读取不再需要 OrderBy。
    /// </summary>
    private static List<LyricLine> ParseCachedLyrics(string text)
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

            var remainder = row[(tab + 1)..];
            var secondTab = remainder.IndexOf('\t');
            var isTimed = true;
            if (secondTab > 0 && remainder[..secondTab] is "0" or "1")
            {
                isTimed = remainder[0] == '1';
                remainder = remainder[(secondTab + 1)..];
            }

            lines.Add(new LyricLine
            {
                IsTimed = isTimed,
                Time = new TimeSpan(ticks),
                Text = DecodeCachedText(remainder.TrimEnd('\r'))
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
                .Append(line.IsTimed ? '1' : '0')
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

    private readonly record struct ProviderOutcome(LyricsSelection? Selection, bool TransientFailure);
}
