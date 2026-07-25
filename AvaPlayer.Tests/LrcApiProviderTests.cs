using AvaPlayer.Models;
using AvaPlayer.Services.Lyrics;

namespace AvaPlayer.Application.Tests;

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
