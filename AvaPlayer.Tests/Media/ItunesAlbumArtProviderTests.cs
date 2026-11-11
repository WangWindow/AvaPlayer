using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AvaPlayer.Models;
using AvaPlayer.Services.AlbumArt;

namespace AvaPlayer.Tests;

public sealed class ItunesAlbumArtProviderTests
{
    [Fact]
    public async Task GetAlbumArtAsync_skips_unrelated_first_result_and_downloads_best_match()
    {
        var searchJson = BuildSearchResponse(
            ("Song", "Other Band", "Album", "https://art.test/wrong/100x100bb.jpg", 180000),
            ("Song", "Artist", "Album", "https://art.test/right/100x100bb.jpg", 180000));
        var artworkBytes = new byte[] { 1, 2, 3, 4 };

        var handler = new RecordingHandler((uri, _) =>
        {
            if (uri.Host == "itunes.apple.com")
            {
                return Task.FromResult(JsonResponse(searchJson));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(artworkBytes)
            });
        });

        var provider = new ItunesAlbumArtProvider(new TestHttpClientFactory(handler));

        var albumArt = await provider.GetAlbumArtAsync(CreateTrack());

        Assert.NotNull(albumArt);
        Assert.Equal(artworkBytes, albumArt);

        var searchRequest = Assert.Single(handler.Requests, uri => uri.Host == "itunes.apple.com");
        Assert.Contains("entity=song", searchRequest.Query);
        Assert.Contains("limit=8", searchRequest.Query);
        Assert.Contains("term=Artist%20Album%20Song", searchRequest.Query);

        var artworkRequest = Assert.Single(handler.Requests, uri => uri.Host == "art.test");
        Assert.Contains("right", artworkRequest.AbsoluteUri);
        Assert.Contains("360x360bb", artworkRequest.AbsoluteUri);
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsoluteUri.Contains("wrong", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAlbumArtAsync_prefers_matching_version_over_earlier_live_variant()
    {
        var searchJson = BuildSearchResponse(
            ("Song (Live)", "Artist", "Album", "https://art.test/live/100x100bb.jpg", 250000),
            ("Song", "Artist", "Album", "https://art.test/studio/100x100bb.jpg", 180000));

        var handler = new RecordingHandler((uri, _) =>
            Task.FromResult(uri.Host == "itunes.apple.com"
                ? JsonResponse(searchJson)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([9])
                }));

        var provider = new ItunesAlbumArtProvider(new TestHttpClientFactory(handler));

        var albumArt = await provider.GetAlbumArtAsync(CreateTrack());

        Assert.NotNull(albumArt);
        var artworkRequest = Assert.Single(handler.Requests, uri => uri.Host == "art.test");
        Assert.Contains("studio", artworkRequest.AbsoluteUri);
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsoluteUri.Contains("live", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAlbumArtAsync_prefers_matching_album_and_duration()
    {
        var searchJson = BuildSearchResponse(
            ("Song", "Artist", "Different Album", "https://art.test/wrong-album/100x100bb.jpg", 250000),
            ("Song", "Artist", "Album", "https://art.test/right-album/100x100bb.jpg", 180000));

        var handler = new RecordingHandler((uri, _) =>
            Task.FromResult(uri.Host == "itunes.apple.com"
                ? JsonResponse(searchJson)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([7])
                }));

        var provider = new ItunesAlbumArtProvider(new TestHttpClientFactory(handler));

        var albumArt = await provider.GetAlbumArtAsync(CreateTrack());

        Assert.NotNull(albumArt);
        var artworkRequest = Assert.Single(handler.Requests, uri => uri.Host == "art.test");
        Assert.Contains("right-album", artworkRequest.AbsoluteUri);
    }

    [Fact]
    public async Task GetAlbumArtAsync_returns_null_without_downloading_when_no_result_matches()
    {
        var searchJson = BuildSearchResponse(
            ("Song", "Other Band", "Different Record", "https://art.test/wrong/100x100bb.jpg", 180000));

        var handler = new RecordingHandler((uri, _) =>
            Task.FromResult(uri.Host == "itunes.apple.com"
                ? JsonResponse(searchJson)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1])
                }));

        var provider = new ItunesAlbumArtProvider(new TestHttpClientFactory(handler));

        var albumArt = await provider.GetAlbumArtAsync(CreateTrack());

        Assert.Null(albumArt);
        Assert.DoesNotContain(handler.Requests, uri => uri.Host == "art.test");
    }

    [Fact]
    public async Task GetAlbumArtAsync_returns_null_for_empty_results()
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(JsonResponse(
            """{"resultCount":0,"results":[]}""")));

        var provider = new ItunesAlbumArtProvider(new TestHttpClientFactory(handler));

        var albumArt = await provider.GetAlbumArtAsync(CreateTrack());

        Assert.Null(albumArt);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetAlbumArtAsync_propagates_transient_search_failure()
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var provider = new ItunesAlbumArtProvider(new TestHttpClientFactory(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetAlbumArtAsync(CreateTrack()));
    }

    [Fact]
    public async Task GetAlbumArtAsync_treats_not_found_search_as_definitive_miss()
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var provider = new ItunesAlbumArtProvider(new TestHttpClientFactory(handler));

        var albumArt = await provider.GetAlbumArtAsync(CreateTrack());

        Assert.Null(albumArt);
    }

    [Fact]
    public async Task GetAlbumArtAsync_propagates_transient_artwork_failure()
    {
        var searchJson = BuildSearchResponse(
            ("Song", "Artist", "Album", "https://art.test/right/100x100bb.jpg", 180000));

        var handler = new RecordingHandler((uri, _) =>
            Task.FromResult(uri.Host == "itunes.apple.com"
                ? JsonResponse(searchJson)
                : new HttpResponseMessage(HttpStatusCode.BadGateway)));

        var provider = new ItunesAlbumArtProvider(new TestHttpClientFactory(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetAlbumArtAsync(CreateTrack()));
    }

    [Fact]
    public async Task GetAlbumArtAsync_treats_not_found_artwork_as_definitive_miss()
    {
        var searchJson = BuildSearchResponse(
            ("Song", "Artist", "Album", "https://art.test/right/100x100bb.jpg", 180000));

        var handler = new RecordingHandler((uri, _) =>
            Task.FromResult(uri.Host == "itunes.apple.com"
                ? JsonResponse(searchJson)
                : new HttpResponseMessage(HttpStatusCode.NotFound)));

        var provider = new ItunesAlbumArtProvider(new TestHttpClientFactory(handler));

        var albumArt = await provider.GetAlbumArtAsync(CreateTrack());

        Assert.Null(albumArt);
    }

    [Fact]
    public async Task GetAlbumArtAsync_rejects_oversized_streamed_artwork_without_full_buffering()
    {
        const long bodyBytes = 16L * 1024 * 1024;
        var stream = new OversizedStream(bodyBytes);
        var searchJson = BuildSearchResponse(
            ("Song", "Artist", "Album", "https://art.test/right/100x100bb.jpg", 180000));

        var handler = new RecordingHandler((uri, _) =>
            Task.FromResult(uri.Host == "itunes.apple.com"
                ? JsonResponse(searchJson)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(stream)
                }));

        var provider = new ItunesAlbumArtProvider(new TestHttpClientFactory(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetAlbumArtAsync(CreateTrack()));

        // The artwork cap is 8 MiB and it is read in 16 KiB chunks, so the full streamed
        // body must not be buffered before the size check runs.
        Assert.True(
            stream.BytesRead <= (8L * 1024 * 1024) + (16 * 1024),
            $"Provider read {stream.BytesRead} bytes; expected the 8 MiB cap to stop it early.");
        Assert.True(stream.BytesRead < bodyBytes);
    }

    private static Track CreateTrack() => new()
    {
        Title = "Song",
        Artist = "Artist",
        Album = "Album",
        DurationSeconds = 180,
        FilePath = "/music/song.mp3"
    };

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    private static string BuildSearchResponse(
        params (string TrackName, string ArtistName, string CollectionName, string ArtworkUrl, int DurationMs)[] songs)
    {
        var items = songs.Select(song => $$"""
            {
              "trackName": {{JsonSerializer.Serialize(song.TrackName)}},
              "artistName": {{JsonSerializer.Serialize(song.ArtistName)}},
              "collectionName": {{JsonSerializer.Serialize(song.CollectionName)}},
              "artworkUrl100": {{JsonSerializer.Serialize(song.ArtworkUrl)}},
              "trackTimeMillis": {{song.DurationMs}}
            }
            """);

        return $$"""{"resultCount":{{songs.Length}},"results":[{{string.Join(",", items)}}]}""";
    }

    private sealed class OversizedStream(long length) : Stream
    {
        private long _remaining = length;
        private readonly long _length = length;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int ReadCore(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, _remaining);
            if (count <= 0)
            {
                return 0;
            }

            buffer[..count].Fill((byte)'x');
            _remaining -= count;
            BytesRead += count;
            return count;
        }
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(
        Func<Uri, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public ConcurrentQueue<Uri> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was missing.");
            Requests.Enqueue(uri);
            return await responder(uri, cancellationToken);
        }
    }
}
