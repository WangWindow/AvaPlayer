using AvaPlayer.Models;
using AvaPlayer.Services.Lyrics;

namespace AvaPlayer.Tests;

public sealed class LrcApiProviderTests
{
    [Fact]
    public async Task GetLyricsAsync_requests_lrc_api_and_parses_lrc_response()
    {
        var handler = new RecordingHandler("[00:01.00]第一句\n[00:02.50]第二句");
        var provider = new LrcApiProvider(new TestHttpClientFactory(handler));
        var track = new Track
        {
            Title = "Song & Title",
            Artist = "Artist",
            Album = "Album",
            FilePath = "/music/song.mp3"
        };

        var lyrics = await provider.GetLyricsAsync(track);

        Assert.NotNull(lyrics);
        Assert.Equal(2, lyrics!.Count);
        Assert.Equal("第一句", lyrics[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(1), lyrics[0].Time);
        Assert.Contains("https://api.lrc.cx/lyrics?", handler.RequestUri!.ToString());
        Assert.Contains("title=Song%20%26%20Title", handler.RequestUri.Query);
        Assert.Contains("artist=Artist", handler.RequestUri.Query);
        Assert.Contains("album=Album", handler.RequestUri.Query);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.NotFound)]
    [InlineData(System.Net.HttpStatusCode.NoContent)]
    public async Task GetLyricsAsync_definitive_no_result_returns_null(System.Net.HttpStatusCode statusCode)
    {
        var provider = new LrcApiProvider(new TestHttpClientFactory(new StatusHandler(statusCode)));

        Assert.Null(await provider.GetLyricsAsync(new Track { Title = "Song", Artist = "Artist" }));
    }

    [Theory]
    [InlineData(System.Net.HttpStatusCode.InternalServerError)]
    [InlineData(System.Net.HttpStatusCode.TooManyRequests)]
    [InlineData(System.Net.HttpStatusCode.BadGateway)]
    public async Task GetLyricsAsync_transient_status_throws(System.Net.HttpStatusCode statusCode)
    {
        var provider = new LrcApiProvider(new TestHttpClientFactory(new StatusHandler(statusCode)));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => provider.GetLyricsAsync(new Track { Title = "Song", Artist = "Artist" }));
    }

    private sealed class StatusHandler(System.Net.HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode));
    }

    private sealed class RecordingHandler(string responseText) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(responseText)
            });
        }
    }
}
