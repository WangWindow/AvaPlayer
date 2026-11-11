using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.AlbumArt;

/// <summary>
/// Composition point for the built-in album-art providers. Adding a provider
/// only requires updating this manager; application DI stays unchanged.
/// </summary>
public sealed class AlbumArtProviderManager(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory) : IAlbumArtProviderManager
{
    public IReadOnlyList<IAlbumArtProvider> Providers { get; } =
        [
            new EmbeddedAlbumArtProvider(loggerFactory.CreateLogger<EmbeddedAlbumArtProvider>()),
            new LrcApiAlbumArtProvider(httpClientFactory),
            new ItunesAlbumArtProvider(httpClientFactory)
        ];
}
