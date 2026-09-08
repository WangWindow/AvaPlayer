using AvaPlayer.Services.Cache;

namespace AvaPlayer.Application.Tests;

public sealed class CacheServiceTests : IDisposable
{
    private readonly string _root;

    public CacheServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"avaplayer-cache-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private CacheService CreateService() => new(_root);

    [Fact]
    public async Task SetText_ThenGetText_RoundTripsAndLeavesNoTempFiles()
    {
        await using var cache = CreateService();

        await cache.SetTextAsync("lyrics", "song-1", "[00:01.00]hello", TimeSpan.FromMinutes(5));
        var value = await cache.GetTextAsync("lyrics", "song-1");

        Assert.Equal("[00:01.00]hello", value);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "lyrics"), "*.tmp"));
    }

    [Fact]
    public async Task SetBytes_ThenGetBytes_RoundTripsAndReturnsCallerOwnedArray()
    {
        await using var cache = CreateService();
        var payload = new byte[] { 1, 2, 3, 4, 5 };

        await cache.SetBytesAsync("album-art", "cover-1", payload);
        payload[0] = 99;
        var read = await cache.GetBytesAsync("album-art", "cover-1");

        Assert.NotNull(read);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, read);
    }

    [Fact]
    public async Task GetText_AfterTtlElapsed_ReturnsNullAndDropsEntry()
    {
        await using var cache = CreateService();

        await cache.SetTextAsync("lyrics", "expiring", "text", TimeSpan.FromMilliseconds(80));
        Assert.Equal("text", await cache.GetTextAsync("lyrics", "expiring"));

        await Task.Delay(200);

        Assert.Null(await cache.GetTextAsync("lyrics", "expiring"));
        var stats = await cache.GetStatsAsync("lyrics");
        Assert.Equal(0, stats.EntryCount);
    }

    [Fact]
    public async Task GetBytes_ServedFromMemoryTier_EvenAfterBackingFileDeleted()
    {
        await using var cache = CreateService();

        await cache.SetTextAsync("lyrics", "hot", "in-memory", TimeSpan.FromMinutes(5));
        var stored = Directory.GetFiles(Path.Combine(_root, "lyrics"), "*.bin").Single();
        File.Delete(stored);

        Assert.Equal("in-memory", await cache.GetTextAsync("lyrics", "hot"));
    }

    [Fact]
    public async Task Tombstone_KnownMissingUntilTtlElapsed()
    {
        await using var cache = CreateService();

        await cache.MarkMissingAsync("lyrics", "no-result", TimeSpan.FromMilliseconds(150));
        Assert.True(await cache.IsKnownMissingAsync("lyrics", "no-result"));
        Assert.Null(await cache.GetTextAsync("lyrics", "no-result"));

        await Task.Delay(250);

        Assert.False(await cache.IsKnownMissingAsync("lyrics", "no-result"));
    }

    [Fact]
    public async Task SetText_OverwritesExistingTombstone()
    {
        await using var cache = CreateService();

        await cache.MarkMissingAsync("lyrics", "key", TimeSpan.FromMinutes(5));
        await cache.SetTextAsync("lyrics", "key", "found later", TimeSpan.FromMinutes(5));

        Assert.False(await cache.IsKnownMissingAsync("lyrics", "key"));
        Assert.Equal("found later", await cache.GetTextAsync("lyrics", "key"));
    }

    [Fact]
    public async Task Writes_OverCapacity_EvictLeastRecentlyUsedInBatches()
    {
        await using var cache = CreateService();
        cache.MaxBytes = 4_000;
        var payload = new byte[1_000];

        for (var i = 0; i < 5; i++)
        {
            await cache.SetBytesAsync("album-art", $"art-{i}", payload);
        }

        var stats = await cache.GetStatsAsync("album-art");
        Assert.True(stats.TotalBytes <= 4_000, $"expected <= 4000, got {stats.TotalBytes}");
        Assert.True(stats.EntryCount < 5, $"expected eviction, still {stats.EntryCount} entries");
        Assert.Null(await cache.GetBytesAsync("album-art", "art-0"));
        Assert.NotNull(await cache.GetBytesAsync("album-art", "art-4"));
    }

    [Fact]
    public async Task HostileKey_StaysInsideCacheRootAndRoundTrips()
    {
        await using var cache = CreateService();
        const string hostileKey = "../../../etc/passwd\u0000evil/|*?";

        await cache.SetTextAsync("lyrics", hostileKey, "safe", TimeSpan.FromMinutes(5));

        Assert.Equal("safe", await cache.GetTextAsync("lyrics", hostileKey));
        Assert.Equal(
            Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith("cache-index.db", StringComparison.Ordinal))
                .Select(f => Path.GetDirectoryName(f))
                .Distinct()
                .Single(),
            Path.Combine(_root, "lyrics"));
    }

    [Fact]
    public async Task InvalidCategory_RejectedWithArgumentException()
    {
        await using var cache = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => cache.SetTextAsync("../escape", "key", "value"));
    }

    [Fact]
    public async Task ClearAsync_WipesFilesMemoryTierAndIndex()
    {
        await using var cache = CreateService();
        await cache.SetTextAsync("lyrics", "a", "text-a");
        await cache.SetTextAsync("album-art", "b", "text-b");
        await cache.MarkMissingAsync("lyrics", "gone", TimeSpan.FromMinutes(5));

        await cache.ClearAsync();

        Assert.True(File.Exists(Path.Combine(_root, "cache-index.db")));
        Assert.Equal(new CacheStats(0, 0), await cache.GetStatsAsync());
        Assert.Null(await cache.GetTextAsync("lyrics", "a"));
        Assert.False(await cache.IsKnownMissingAsync("lyrics", "gone"));
    }

    [Fact]
    public async Task ClearCategoryAsync_WipesOnlyThatCategory()
    {
        await using var cache = CreateService();
        await cache.SetTextAsync("lyrics", "a", "text-a");
        await cache.SetTextAsync("album-art", "b", "text-b");

        await cache.ClearCategoryAsync("lyrics");

        Assert.Null(await cache.GetTextAsync("lyrics", "a"));
        Assert.Equal("text-b", await cache.GetTextAsync("album-art", "b"));
        Assert.Equal(0, (await cache.GetStatsAsync("lyrics")).EntryCount);
        Assert.Equal(1, (await cache.GetStatsAsync("album-art")).EntryCount);
    }

    [Fact]
    public async Task LegacyGetFilePath_StillResolvesUnderCategoryDirectory()
    {
        await using var cache = CreateService();

        var path = cache.GetFilePath("lyrics", "legacy-key.cache");

        Assert.Equal(Path.Combine(_root, "lyrics", "legacy-key.cache"), path);
        Assert.True(Directory.Exists(Path.Combine(_root, "lyrics")));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ConcurrentSetsAndGets_AllRemainConsistent()
    {
        await using var cache = CreateService();
        var keys = Enumerable.Range(0, 32).Select(i => $"key-{i}").ToArray();

        await Task.WhenAll(keys.Select(async key =>
        {
            await cache.SetTextAsync("lyrics", key, $"value-{key}", TimeSpan.FromMinutes(5));
            await cache.GetTextAsync("lyrics", key);
        }));

        foreach (var key in keys)
        {
            Assert.Equal($"value-{key}", await cache.GetTextAsync("lyrics", key));
        }
    }
}
