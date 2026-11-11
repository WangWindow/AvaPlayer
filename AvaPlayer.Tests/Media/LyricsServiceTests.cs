using System.Net;
using System.Text;
using AvaPlayer.Models;
using AvaPlayer.Services.Cache;
using AvaPlayer.Services.Lyrics;
using AvaPlayer.Services.Network;
using AvaPlayer.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Tests;

public sealed class LyricsServiceTests : IDisposable
{
    private readonly TestDirectory _directory = new();
    private readonly CacheService _cache;
    private readonly string _trackPath;

    public LyricsServiceTests()
    {
        _cache = new CacheService(_directory.FilePath("cache"));
        _trackPath = _directory.CreateWave();
    }

    public void Dispose()
    {
        _cache.Dispose();
        _directory.Dispose();
    }

    [Fact]
    public async Task Transient_provider_failure_is_not_cached_as_missing()
    {
        var call = 0;
        var provider = new StubLyricsProvider("Flaky", (_, _) =>
            Interlocked.Increment(ref call) == 1
                ? throw new HttpRequestException("provider unavailable")
                : Task.FromResult<IReadOnlyList<LyricLine>?>(BuildTimedLyrics()));
        var service = CreateService(provider);

        var first = await service.GetLyricsAsync(CreateTrack());
        var second = await service.GetLyricsAsync(CreateTrack());

        Assert.Empty(first);
        Assert.Equal(8, second.Count);
        Assert.All(second, line => Assert.True(line.IsTimed));
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public async Task Definitive_provider_miss_is_remembered_and_skips_retry()
    {
        var provider = new StubLyricsProvider(
            "Empty",
            (_, _) => Task.FromResult<IReadOnlyList<LyricLine>?>(null));
        var service = CreateService(provider);

        var track = CreateTrack();
        Assert.Empty(await service.GetLyricsAsync(track));
        Assert.Equal(1, provider.Calls);

        Assert.Empty(await service.GetLyricsAsync(track));

        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Cache_write_failure_does_not_discard_fetched_lyrics()
    {
        var provider = new StubLyricsProvider(
            "Fetched",
            (_, _) => Task.FromResult<IReadOnlyList<LyricLine>?>(BuildTimedLyrics()));
        var service = new LyricsService(
            new ThrowingTextCache(),
            new TestProviders(provider),
            new OnlineNetwork(),
            NullLogger<LyricsService>.Instance);

        var result = await service.GetLyricsAsync(CreateTrack());

        Assert.Equal(8, result.Count);
        Assert.All(result, line => Assert.True(line.IsTimed));
    }

    [Fact]
    public async Task Estimated_plain_lyrics_round_trip_through_cache_as_untimed()
    {
        var provider = new StubLyricsProvider(
            "Plain",
            (_, _) => Task.FromResult<IReadOnlyList<LyricLine>?>(BuildPlainLyrics()));
        var service = CreateService(provider);

        var first = await service.GetLyricsAsync(CreateTrack());
        var second = await service.GetLyricsAsync(CreateTrack());

        Assert.Equal(8, first.Count);
        Assert.All(first, line => Assert.False(line.IsTimed));
        Assert.All(second, line => Assert.False(line.IsTimed));
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Synchronized_lyrics_win_over_faster_estimated_plain_lyrics()
    {
        var plain = new StubLyricsProvider(
            "Fast plain",
            (_, _) => Task.FromResult<IReadOnlyList<LyricLine>?>(BuildPlainLyrics(prefix: "plain")));
        var timed = new StubLyricsProvider(
            "Slower synced",
            async (_, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                return BuildTimedLyrics(prefix: "synced");
            });
        var service = CreateService(plain, timed);

        var result = await service.GetLyricsAsync(CreateTrack());

        Assert.Equal(8, result.Count);
        Assert.All(result, line => Assert.True(line.IsTimed));
        Assert.StartsWith("synced", result[0].Text, StringComparison.Ordinal);
        Assert.Equal(1, plain.Calls);
        Assert.Equal(1, timed.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_is_observed_promptly_during_provider_race()
    {
        var provider = new BlockingLyricsProvider();
        var service = CreateService(provider);
        using var cancellation = new CancellationTokenSource();

        var lookup = service.GetLyricsAsync(CreateTrack(), cancellation.Token);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        try
        {
            var completed = await Task.WhenAny(lookup, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.Same(lookup, completed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        }
        finally
        {
            provider.Release.TrySetResult(null);
        }
    }

    [Fact]
    public async Task Lrclib_plain_lyrics_are_marked_untimed()
    {
        var handler = new StubHttpHandler(
            HttpStatusCode.OK,
            """{"plainLyrics":"first line\nsecond line\nthird line"}""");
        var provider = new LrcLibProvider(new TestHttpClientFactory(handler));

        var lyrics = await provider.GetLyricsAsync(CreateTrack());

        Assert.NotNull(lyrics);
        Assert.Equal(3, lyrics!.Count);
        Assert.All(lyrics, line => Assert.False(line.IsTimed));
    }

    [Fact]
    public async Task Lrclib_not_found_is_a_definitive_miss()
    {
        var provider = new LrcLibProvider(new TestHttpClientFactory(new StubHttpHandler(HttpStatusCode.NotFound)));

        Assert.Null(await provider.GetLyricsAsync(CreateTrack()));
    }

    [Fact]
    public async Task Lrclib_transient_status_propagates_as_failure()
    {
        var provider = new LrcLibProvider(new TestHttpClientFactory(new StubHttpHandler(HttpStatusCode.ServiceUnavailable)));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetLyricsAsync(CreateTrack()));
    }

    [Fact]
    public async Task Lyrics_ovh_plain_lyrics_are_marked_untimed()
    {
        var handler = new StubHttpHandler(
            HttpStatusCode.OK,
            """{"lyrics":"first line\nsecond line\nthird line"}""");
        var provider = new LyricsOvhProvider(new TestHttpClientFactory(handler));

        var lyrics = await provider.GetLyricsAsync(CreateTrack());

        Assert.NotNull(lyrics);
        Assert.Equal(3, lyrics!.Count);
        Assert.All(lyrics, line => Assert.False(line.IsTimed));
    }

    [Fact]
    public async Task Lyrics_ovh_not_found_is_a_definitive_miss()
    {
        var provider = new LyricsOvhProvider(new TestHttpClientFactory(new StubHttpHandler(HttpStatusCode.NotFound)));

        Assert.Null(await provider.GetLyricsAsync(CreateTrack()));
    }

    private LyricsService CreateService(params ILyricsProvider[] providers) =>
        new(_cache, new TestProviders(providers), new OnlineNetwork(), NullLogger<LyricsService>.Instance);

    private Track CreateTrack() => new()
    {
        FilePath = _trackPath,
        Title = "Song",
        Artist = "Artist",
        Album = "Album",
        DurationSeconds = 60
    };

    private static LyricLine[] BuildTimedLyrics(int count = 8, string prefix = "timed") =>
        Enumerable.Range(0, count)
            .Select(index => new LyricLine
            {
                IsTimed = true,
                Time = TimeSpan.FromSeconds(index * 7.5),
                Text = $"{prefix} {index}"
            })
            .ToArray();

    private static LyricLine[] BuildPlainLyrics(int count = 8, string prefix = "plain") =>
        Enumerable.Range(0, count)
            .Select(index => new LyricLine
            {
                IsTimed = false,
                Time = TimeSpan.FromSeconds(index * 7.5),
                Text = $"{prefix} {index}"
            })
            .ToArray();

    private sealed class TestProviders(params ILyricsProvider[] providers) : ILyricsProviderManager
    {
        public IReadOnlyList<ILyricsProvider> Providers { get; } = providers;
    }

    private sealed class StubLyricsProvider(
        string name,
        Func<Track, CancellationToken, Task<IReadOnlyList<LyricLine>?>> handler) : ILyricsProvider
    {
        public int Calls { get; private set; }

        public string Name { get; } = name;

        public Task<IReadOnlyList<LyricLine>?> GetLyricsAsync(
            Track track,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return handler(track, cancellationToken);
        }
    }

    private sealed class BlockingLyricsProvider : ILyricsProvider
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<IReadOnlyList<LyricLine>?> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "Blocking";

        public async Task<IReadOnlyList<LyricLine>?> GetLyricsAsync(
            Track track,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            // Intentionally ignores cancellation: the service must not depend on provider cooperation
            // to notice that the caller changed tracks.
            return await Release.Task;
        }
    }

    private sealed class OnlineNetwork : INetworkAccessService
    {
        public bool IsNetworkEnabled { get; set; } = true;
        public bool IsEnabled => IsNetworkEnabled;
        public event EventHandler<bool>? NetworkAccessChanged { add { } remove { } }
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PersistAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingTextCache : ICacheService
    {
        public string RootPath => Path.GetTempPath();

        public long MaxBytes { get; set; }

        public string GetCategoryPath(string category) => throw new NotSupportedException();

        public string GetFilePath(string category, string fileName) => throw new NotSupportedException();

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearCategoryAsync(string category, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string?> GetTextAsync(
            string category,
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task SetTextAsync(
            string category,
            string key,
            string value,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException(new IOException("cache volume is read-only"));

        public Task<byte[]?> GetBytesAsync(
            string category,
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task SetBytesAsync(
            string category,
            string key,
            byte[] value,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task MarkMissingAsync(
            string category,
            string key,
            TimeSpan ttl,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> IsKnownMissingAsync(
            string category,
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<CacheStats> GetStatsAsync(
            string? category = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CacheStats());

        public Task TrimAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpHandler(HttpStatusCode statusCode, string? content = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode);
            if (content is not null)
            {
                response.Content = new StringContent(content, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }
    }
}
