using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.AlbumArt;

/// <summary>
/// Composition point for the built-in album-art providers. Adding a provider
/// only requires updating this manager; application DI stays unchanged.
/// </summary>
public sealed class AlbumArtProviderManager : IAlbumArtProviderManager
{
    public AlbumArtProviderManager(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        Providers =
        [
            new EmbeddedAlbumArtProvider(loggerFactory.CreateLogger<EmbeddedAlbumArtProvider>()),
            new LrcApiAlbumArtProvider(httpClientFactory),
            new ItunesAlbumArtProvider(httpClientFactory)
        ];
    }

    public IReadOnlyList<IAlbumArtProvider> Providers { get; }
}
