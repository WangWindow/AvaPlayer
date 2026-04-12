using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AvaPlayer.Helpers;
using AvaPlayer.Models;
using AvaPlayer.Services.Cache;

namespace AvaPlayer.Services.Lyrics;

public sealed class LyricsService : ILyricsService
{
    private const string CacheKeyVersion = "lyrics-v2";

    private readonly ICacheService _cacheService;
    private readonly IReadOnlyList<ILyricsProvider> _providers;

    public LyricsService(ICacheService cacheService, IEnumerable<ILyricsProvider> providers)
    {
        _cacheService = cacheService;
        _providers = providers.ToArray();
    }

    public async Task<IReadOnlyList<LyricLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        var sidecarPath = Path.ChangeExtension(track.FilePath, ".lrc");
        if (File.Exists(sidecarPath))
        {
            var sidecarText = await File.ReadAllTextAsync(sidecarPath, cancellationToken);
            return LrcParser.Parse(sidecarText);
        }

        var cachePath = _cacheService.GetFilePath("lyrics", $"{BuildCacheKey(track)}.cache");
        LyricsSelection? bestFallback = null;
        if (File.Exists(cachePath))
        {
            var cached = await ReadCacheAsync(cachePath, cancellationToken);
            if (cached.Count > 0)
            {
                var cachedSelection = CreateSelection(track, cached);
                if (cachedSelection.Quality.IsAccepted)
                {
                    return cached;
                }

                bestFallback = cachedSelection;
            }
        }

        LyricsSelection? bestAccepted = null;
        foreach (var provider in _providers)
        {
            var lyrics = await provider.GetLyricsAsync(track, cancellationToken);
            if (lyrics is not { Count: > 0 })
            {
                continue;
            }

            var selection = CreateSelection(track, lyrics);
            if (selection.Quality.IsAccepted)
            {
                if (!bestAccepted.HasValue || selection.Quality.Score > bestAccepted.Value.Quality.Score)
                {
                    bestAccepted = selection;
                }

                continue;
            }

            if (!bestFallback.HasValue || selection.Quality.Score > bestFallback.Value.Quality.Score)
            {
                bestFallback = selection;
            }
        }

        if (bestAccepted.HasValue)
        {
            await WriteCacheAsync(cachePath, bestAccepted.Value.Lyrics, cancellationToken);
            return bestAccepted.Value.Lyrics;
        }

        return bestFallback?.Lyrics ?? [];
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

    private static async Task<IReadOnlyList<LyricLine>> ReadCacheAsync(string cachePath, CancellationToken cancellationToken)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(cachePath, cancellationToken);
            return lines
                .Select(static line => line.Split('\t', 2))
                .Where(static parts => parts.Length == 2 && long.TryParse(parts[0], out _))
                .Select(static parts => new LyricLine
                {
                    Time = new TimeSpan(long.Parse(parts[0], CultureInfo.InvariantCulture)),
                    Text = DecodeCachedText(parts[1])
                })
                .OrderBy(static line => line.Time)
                .ToArray();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lyrics] 读取缓存歌词失败: {ex.Message}");
            return [];
        }
    }

    private static async Task WriteCacheAsync(string cachePath, IReadOnlyList<LyricLine> lyrics, CancellationToken cancellationToken)
    {
        var text = string.Join(
            Environment.NewLine,
            lyrics.Select(static line =>
                $"{line.Time.Ticks}\t{Convert.ToBase64String(Encoding.UTF8.GetBytes(line.Text))}"));

        await File.WriteAllTextAsync(cachePath, text, cancellationToken);
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
