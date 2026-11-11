using System.Net;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AvaPlayer.Models;
using AvaPlayer.Services.AlbumArt;
using AvaPlayer.Services.Cache;
using AvaPlayer.Services.Network;
using AvaPlayer.Tests.Support;
using AvaPlayer.Tests.UI;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Tests;

[Collection("UI")]
public sealed class AlbumArtServiceTests : IDisposable
{
    private readonly HeadlessUnitTestSession _ui = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

    [Fact]
    public Task Legacy_art_cache_entries_are_not_reused_after_matching_changes() => _ui.Dispatch(async () =>
    {
        using var directory = new TestDirectory();
        using var cache = new CacheService(directory.FilePath("cache"));
        var track = new Track
        {
            FilePath = "/music/song.mp3",
            Title = "Song",
            Artist = "Artist",
            Album = "Album"
        };
        var legacyKey = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
            $"{track.DisplayArtist}|{track.DisplayAlbum}|{track.DisplayTitle}")));
        await cache.SetBytesAsync("album-art", legacyKey, CreatePng(2, 1));
        var provider = new StubArtProvider("Fresh matching result", (_, _) => Task.FromResult<byte[]?>(CreatePng(3, 1)));
        var service = CreateService(cache, provider);

        using var art = await service.GetAlbumArtAsync(track);

        Assert.NotNull(art);
        Assert.Equal(3, art!.PixelSize.Width / art.PixelSize.Height);
        Assert.Equal(1, provider.Calls);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Untagged_tracks_with_same_display_name_use_distinct_cache_entries() => _ui.Dispatch(async () =>
    {
        using var directory = new TestDirectory();
        using var cache = new CacheService(directory.FilePath("cache"));
        var provider = new PerTrackCover(new Dictionary<string, byte[]>
        {
            ["/music/a/song.mp3"] = CreatePng(2, 1),
            ["/music/b/song.mp3"] = CreatePng(3, 1)
        });
        var service = CreateService(cache, provider);
        var first = new Track { FilePath = "/music/a/song.mp3" };
        var second = new Track { FilePath = "/music/b/song.mp3" };

        using (var firstArt = await service.GetAlbumArtAsync(first))
        {
            Assert.NotNull(firstArt);
            Assert.Equal(2, firstArt!.PixelSize.Width / firstArt.PixelSize.Height);
        }

        using (var secondArt = await service.GetAlbumArtAsync(second))
        {
            Assert.NotNull(secondArt);
            Assert.Equal(3, secondArt!.PixelSize.Width / secondArt.PixelSize.Height);
        }

        Assert.Equal(2, provider.Calls);

        // The first track must still come from its own cache entry, not the second track's bytes.
        using (var cachedFirst = await service.GetAlbumArtAsync(first))
        {
            Assert.NotNull(cachedFirst);
            Assert.Equal(2, cachedFirst!.PixelSize.Width / cachedFirst.PixelSize.Height);
        }

        Assert.Equal(2, provider.Calls);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Transient_cover_failure_is_not_cached_as_missing() => _ui.Dispatch(async () =>
    {
        using var directory = new TestDirectory();
        using var cache = new CacheService(directory.FilePath("cache"));
        var providerCalls = 0;
        var provider = new StubArtProvider("Flaky", (_, _) =>
            Interlocked.Increment(ref providerCalls) == 1
                ? throw new HttpRequestException("provider unavailable")
                : Task.FromResult<byte[]?>(CreatePng(1, 1)));
        var service = CreateService(cache, provider);
        var track = new Track { FilePath = "/music/song.mp3", Title = "Song", Artist = "Artist", Album = "Album" };

        using (var first = await service.GetAlbumArtAsync(track))
        {
            Assert.Null(first);
        }

        using (var second = await service.GetAlbumArtAsync(track))
        {
            Assert.NotNull(second);
        }

        Assert.Equal(2, provider.Calls);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Definitive_cover_miss_is_remembered_and_skips_retry() => _ui.Dispatch(async () =>
    {
        using var directory = new TestDirectory();
        using var cache = new CacheService(directory.FilePath("cache"));
        var provider = new StubArtProvider("Empty", (_, _) => Task.FromResult<byte[]?>(null));
        var service = CreateService(cache, provider);
        var track = new Track { FilePath = "/music/song.mp3", Title = "Song", Artist = "Artist", Album = "Album" };

        using (var first = await service.GetAlbumArtAsync(track))
        {
            Assert.Null(first);
        }

        using var second = await service.GetAlbumArtAsync(track);
        Assert.Null(second);
        Assert.Equal(1, provider.Calls);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Cache_write_failure_does_not_discard_decoded_cover() => _ui.Dispatch(async () =>
    {
        var provider = new StubArtProvider("Fetched", (_, _) => Task.FromResult<byte[]?>(CreatePng(1, 1)));
        var service = new AlbumArtService(
            new ThrowingBytesCache(),
            new TestProviders(provider),
            new OnlineNetwork(),
            NullLogger<AlbumArtService>.Instance);

        using var art = await service.GetAlbumArtAsync(
            new Track { FilePath = "/music/song.mp3", Title = "Song", Artist = "Artist", Album = "Album" });

        Assert.NotNull(art);
        Assert.Equal(1, provider.Calls);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Caller_cancellation_is_observed_promptly_during_provider_race() => _ui.Dispatch(async () =>
    {
        using var directory = new TestDirectory();
        using var cache = new CacheService(directory.FilePath("cache"));
        var provider = new BlockingArtProvider();
        var service = CreateService(cache, provider);
        var track = new Track { FilePath = "/music/song.mp3", Title = "Song", Artist = "Artist", Album = "Album" };
        using var cancellation = new CancellationTokenSource();

        var lookup = service.GetAlbumArtAsync(track, cancellation.Token);
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

        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task LrcApi_provider_definitive_no_result_returns_null(HttpStatusCode statusCode)
    {
        var provider = new LrcApiAlbumArtProvider(
            new TestHttpClientFactory(new StatusHandler(statusCode)));

        Assert.Null(await provider.GetAlbumArtAsync(new Track { Title = "Song", Artist = "Artist" }));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task LrcApi_provider_transient_status_throws(HttpStatusCode statusCode)
    {
        var provider = new LrcApiAlbumArtProvider(
            new TestHttpClientFactory(new StatusHandler(statusCode)));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetAlbumArtAsync(new Track { Title = "Song", Artist = "Artist" }));
    }

    [Fact]
    public async Task LrcApi_provider_non_image_success_throws_instead_of_tombstoning()
    {
        var provider = new LrcApiAlbumArtProvider(
            new TestHttpClientFactory(new TextContentHandler()));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetAlbumArtAsync(new Track { Title = "Song", Artist = "Artist" }));
    }

    private static AlbumArtService CreateService(ICacheService cache, params IAlbumArtProvider[] providers) =>
        new(cache, new TestProviders(providers), new OnlineNetwork(), NullLogger<AlbumArtService>.Instance);

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    public void Dispose() => _ui.Dispose();

    private sealed class TestProviders(params IAlbumArtProvider[] providers) : IAlbumArtProviderManager
    {
        public IReadOnlyList<IAlbumArtProvider> Providers { get; } = providers;
    }

    private sealed class PerTrackCover(IReadOnlyDictionary<string, byte[]> covers) : IAlbumArtProvider
    {
        public int Calls { get; private set; }

        public string Name => "Per-track cover";

        public bool RequiresNetwork => true;

        public Task<byte[]?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(covers.TryGetValue(track.FilePath, out var bytes) ? bytes : null);
        }
    }

    private sealed class StubArtProvider(
        string name,
        Func<Track, CancellationToken, Task<byte[]?>> handler) : IAlbumArtProvider
    {
        public int Calls { get; private set; }

        public string Name { get; } = name;

        public bool RequiresNetwork => true;

        public Task<byte[]?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default)
        {
            Calls++;
            return handler(track, cancellationToken);
        }
    }

    private sealed class BlockingArtProvider : IAlbumArtProvider
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<byte[]?> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "Blocking";

        public bool RequiresNetwork => true;

        public async Task<byte[]?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            // Intentionally ignores cancellation: the service must not depend on provider cooperation.
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

    private sealed class ThrowingBytesCache : ICacheService
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
            Task.CompletedTask;

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
            Task.FromException(new IOException("cache volume is read-only"));

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

    private sealed class StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class TextContentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>upstream error</html>", System.Text.Encoding.UTF8, "text/html")
            });
    }
}
