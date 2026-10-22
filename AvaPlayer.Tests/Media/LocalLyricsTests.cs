using AvaPlayer.Helpers;
using AvaPlayer.Models;
using AvaPlayer.Services.Cache;
using AvaPlayer.Services.Lyrics;
using AvaPlayer.Services.Network;
using AvaPlayer.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Tests;

public sealed class LocalLyricsTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private readonly CacheService _cache;
    private readonly LyricsService _lyrics;
    private readonly CountingProvider _provider = new();

    public LocalLyricsTests()
    {
        _cache = new CacheService(_directory.FilePath("cache"));
        _lyrics = new LyricsService(_cache, new TestProviders(_provider), new OfflineNetwork(), NullLogger<LyricsService>.Instance);
    }

    [Fact]
    public async Task Embedded_lyrics_take_priority_over_sidecar_and_network()
    {
        var path = _directory.CreateWave(lyrics: "[00:01.00]Embedded lyrics");
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), "[00:01.00]Sidecar");
        var result = await _lyrics.GetLyricsAsync(new Track { FilePath = path });
        Assert.Equal("Embedded lyrics", Assert.Single(result).Text);
        Assert.Equal(0, _provider.Calls);
    }

    [Fact]
    public async Task Updated_embedded_lyrics_are_visible_without_cache_expiry()
    {
        var path = _directory.CreateWave(lyrics: "[00:01.00]Old");
        var track = new Track { FilePath = path };
        await _lyrics.GetLyricsAsync(track);
        using (var file = TagLib.File.Create(path))
        {
            file.Tag.Lyrics = "[00:01.00]New";
            file.Save();
        }
        Assert.Equal("New", Assert.Single(await _lyrics.GetLyricsAsync(track)).Text);
    }

    [Fact]
    public async Task Empty_sidecar_does_not_hide_embedded_lyrics()
    {
        var path = _directory.CreateWave(lyrics: "Plain text\nSecond line");
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".lrc"), "");
        var result = await _lyrics.GetLyricsAsync(new Track { FilePath = path });
        Assert.Equal(2, result.Count);
        Assert.All(result, line => Assert.False(line.IsTimed));
    }

    [Fact]
    public void Local_plain_lyrics_are_not_marked_as_seekable()
    {
        Assert.All(LrcParser.ParseLocal("First\nSecond"), line => Assert.False(line.IsTimed));
        Assert.True(Assert.Single(LrcParser.ParseLocal("[00:01.00]Timed")).IsTimed);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _directory.Dispose();
    }

    private sealed class TestProviders(ILyricsProvider provider) : ILyricsProviderManager
    {
        public IReadOnlyList<ILyricsProvider> Providers { get; } = [provider];
    }

    private sealed class CountingProvider : ILyricsProvider
    {
        public int Calls { get; private set; }
        public string Name => "Test";
        public Task<IReadOnlyList<LyricLine>?> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<LyricLine>?>([]);
        }
    }

    private sealed class OfflineNetwork : INetworkAccessService
    {
        public bool IsEnabled => false;
        public bool IsNetworkEnabled { get; set; }
        public event EventHandler<bool>? NetworkAccessChanged { add { } remove { } }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PersistAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
