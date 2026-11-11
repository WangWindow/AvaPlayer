using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AvaPlayer.Models;
using AvaPlayer.Services.Lyrics;

namespace AvaPlayer.Tests;

public sealed class NetEaseProviderTests
{
    private const string EmptyLyrics = """{"lrc":{"lyric":""}}""";

    [Fact]
    public async Task GetLyricsAsync_fetches_candidates_concurrently_and_cancels_slow_candidates()
    {
        var searchJson = BuildSearchResponse(
            (1, "Song", "Artist", "Album", 180000),
            (2, "Song", "Artist", "Album", 190000));
        var wantedLyrics = BuildLyricResponse(
            "[00:00.00]first\n[00:45.00]second\n[01:30.00]third\n[02:40.00]wanted");

        var handler = new RecordingHandler(async (uri, cancellationToken) =>
        {
            if (uri.AbsolutePath.Contains("cloudsearch", StringComparison.Ordinal))
            {
                return JsonResponse(searchJson);
            }

            if (GetSongId(uri) == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return GetSongId(uri) == 2
                ? JsonResponse(wantedLyrics)
                : JsonResponse(EmptyLyrics);
        });

        var provider = new NetEaseProvider(new TestHttpClientFactory(handler));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        var lyrics = await provider.GetLyricsAsync(CreateTrack(), timeout.Token);

        Assert.NotNull(lyrics);
        Assert.Contains(lyrics!, line => line.Text == "wanted");
        Assert.Contains(handler.Requests, uri => GetSongId(uri) == 1);
        Assert.Contains(handler.Requests, uri => GetSongId(uri) == 2);
    }

    [Fact]
    public async Task GetLyricsAsync_falls_back_to_title_artist_search_when_primary_has_no_usable_candidates()
    {
        var primaryJson = BuildSearchResponse(
            (1, "Completely Different", "Other Band", "Album", 180000));
        var fallbackJson = BuildSearchResponse(
            (2, "Song", "Artist", "Album", 180000));
        var lyricsJson = BuildLyricResponse(
            "[00:00.00]first\n[00:45.00]second\n[01:30.00]third\n[02:40.00]fourth");

        var handler = new RecordingHandler((uri, _) =>
        {
            if (!uri.AbsolutePath.Contains("cloudsearch", StringComparison.Ordinal))
            {
                return Task.FromResult(GetSongId(uri) == 2
                    ? JsonResponse(lyricsJson)
                    : JsonResponse(EmptyLyrics));
            }

            var query = Uri.UnescapeDataString(uri.Query);
            return Task.FromResult(query.Contains("Album", StringComparison.Ordinal)
                ? JsonResponse(primaryJson)
                : JsonResponse(fallbackJson));
        });

        var provider = new NetEaseProvider(new TestHttpClientFactory(handler));

        var lyrics = await provider.GetLyricsAsync(CreateTrack());

        Assert.NotNull(lyrics);
        var searches = handler.Requests
            .Where(uri => uri.AbsolutePath.Contains("cloudsearch", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, searches.Length);

        var primaryQuery = Uri.UnescapeDataString(searches[0].Query);
        var fallbackQuery = Uri.UnescapeDataString(searches[1].Query);
        Assert.Contains("type=1", searches[0].Query);
        Assert.Contains("limit=10", searches[0].Query);
        Assert.Contains("Album", primaryQuery);
        Assert.DoesNotContain("Album", fallbackQuery);
    }

    [Fact]
    public async Task GetLyricsAsync_does_not_fall_back_when_primary_query_has_usable_candidates()
    {
        var searchJson = BuildSearchResponse((1, "Song", "Artist", "Album", 180000));
        var handler = new RecordingHandler((uri, _) =>
            Task.FromResult(uri.AbsolutePath.Contains("cloudsearch", StringComparison.Ordinal)
                ? JsonResponse(searchJson)
                : JsonResponse(BuildLyricResponse("[00:00.00]first\n[00:45.00]second\n[01:30.00]third\n[02:40.00]fourth"))));

        var provider = new NetEaseProvider(new TestHttpClientFactory(handler));

        var lyrics = await provider.GetLyricsAsync(CreateTrack());

        Assert.NotNull(lyrics);
        Assert.Single(handler.Requests, uri =>
            uri.AbsolutePath.Contains("cloudsearch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetLyricsAsync_caps_lyric_candidate_fetches()
    {
        var songs = Enumerable.Range(1, 8)
            .Select(id => ((long)id, "Song", "Artist", "Album", 180000))
            .ToArray();
        var searchJson = BuildSearchResponse(songs);

        var handler = new RecordingHandler((uri, _) =>
            Task.FromResult(uri.AbsolutePath.Contains("cloudsearch", StringComparison.Ordinal)
                ? JsonResponse(searchJson)
                : JsonResponse(EmptyLyrics)));

        var provider = new NetEaseProvider(new TestHttpClientFactory(handler));

        var lyrics = await provider.GetLyricsAsync(CreateTrack());

        Assert.Null(lyrics);
        Assert.Single(handler.Requests, uri =>
            uri.AbsolutePath.Contains("cloudsearch", StringComparison.Ordinal));
        Assert.Equal(3, handler.Requests.Count(uri =>
            uri.AbsolutePath.Contains("/api/song/lyric", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task GetLyricsAsync_propagates_transient_search_failure()
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var provider = new NetEaseProvider(new TestHttpClientFactory(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetLyricsAsync(CreateTrack()));
    }

    [Fact]
    public async Task GetLyricsAsync_propagates_transient_lyric_failure()
    {
        var searchJson = BuildSearchResponse((1, "Song", "Artist", "Album", 180000));
        var handler = new RecordingHandler((uri, _) =>
            Task.FromResult(uri.AbsolutePath.Contains("cloudsearch", StringComparison.Ordinal)
                ? JsonResponse(searchJson)
                : new HttpResponseMessage(HttpStatusCode.BadGateway)));

        var provider = new NetEaseProvider(new TestHttpClientFactory(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetLyricsAsync(CreateTrack()));
    }

    [Fact]
    public async Task GetLyricsAsync_treats_not_found_as_definitive_miss()
    {
        var handler = new RecordingHandler((uri, _) =>
            Task.FromResult(uri.AbsolutePath.Contains("cloudsearch", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.NotFound)));

        var provider = new NetEaseProvider(new TestHttpClientFactory(handler));

        var lyrics = await provider.GetLyricsAsync(CreateTrack());

        Assert.Null(lyrics);
    }

    [Fact]
    public async Task GetLyricsAsync_rejects_oversized_streamed_search_body_without_full_buffering()
    {
        const long bodyBytes = 8L * 1024 * 1024;
        var stream = new OversizedStream(bodyBytes);
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }));

        var provider = new NetEaseProvider(new TestHttpClientFactory(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetLyricsAsync(CreateTrack()));

        // The provider's cap is 2 MiB and it reads in 16 KiB chunks, so it must stop near
        // the cap instead of buffering the whole streamed body.
        Assert.True(
            stream.BytesRead <= (2L * 1024 * 1024) + (16 * 1024),
            $"Provider read {stream.BytesRead} bytes; expected the 2 MiB cap to stop it early.");
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

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string BuildSearchResponse(
        params (long Id, string Title, string Artist, string Album, int DurationMs)[] songs)
    {
        var items = songs.Select(song => $$"""
            {
              "id": {{song.Id}},
              "name": {{JsonSerializer.Serialize(song.Title)}},
              "alia": [],
              "tns": [],
              "ar": [{"name": {{JsonSerializer.Serialize(song.Artist)}}}],
              "al": {"name": {{JsonSerializer.Serialize(song.Album)}}},
              "dt": {{song.DurationMs}}
            }
            """);

        return "{\"result\":{\"songs\":[" + string.Join(",", items) + "]}}";
    }

    private static string BuildLyricResponse(string lyricText) =>
        JsonSerializer.Serialize(new { lrc = new { lyric = lyricText } });

    private static long GetSongId(Uri uri)
    {
        var match = Regex.Match(uri.Query, @"[?&]id=(?<id>\d+)");
        return match.Success
            ? long.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture)
            : 0;
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
