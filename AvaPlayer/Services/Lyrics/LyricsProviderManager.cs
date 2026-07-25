namespace AvaPlayer.Services.Lyrics;

/// <summary>
/// Composition point for the built-in lyric providers. Adding a provider only
/// requires updating this manager; application DI stays unchanged.
/// </summary>
public sealed class LyricsProviderManager : ILyricsProviderManager
{
    public LyricsProviderManager(IHttpClientFactory httpClientFactory)
    {
        Providers =
        [
            new LrcLibProvider(httpClientFactory),
            new LrcApiProvider(httpClientFactory),
            new NetEaseProvider(httpClientFactory),
            new LyricsOvhProvider(httpClientFactory)
        ];
    }

    public IReadOnlyList<ILyricsProvider> Providers { get; }
}
