using System.Security.Cryptography;
using System.Text;
using AvaPlayer.Helpers;
using AvaPlayer.Models;
using AvaPlayer.Services.Cache;

namespace AvaPlayer.Services.Lyrics;

public sealed class LyricsService : ILyricsService
{
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
        if (File.Exists(cachePath))
        {
            var cached = await ReadCacheAsync(cachePath, cancellationToken);
            if (cached.Count > 0)
            {
                return cached;
            }
        }

        foreach (var provider in _providers)
        {
            var lyrics = await provider.GetLyricsAsync(track, cancellationToken);
            if (lyrics is { Count: > 0 })
            {
                await WriteCacheAsync(cachePath, lyrics, cancellationToken);
                return lyrics;
            }
        }

        return [];
    }

    private static string BuildCacheKey(Track track)
    {
        var bytes = Encoding.UTF8.GetBytes($"{track.DisplayArtist}|{track.DisplayTitle}|{track.DisplayAlbum}");
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
                    Time = TimeSpan.FromTicks(long.Parse(parts[0])),
                    Text = parts[1]
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
            lyrics.Select(line => $"{line.Time.Ticks}\t{line.Text.Replace('\t', ' ')}"));

        await File.WriteAllTextAsync(cachePath, text, cancellationToken);
    }
}
