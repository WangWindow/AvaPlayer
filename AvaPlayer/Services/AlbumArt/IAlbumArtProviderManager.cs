namespace AvaPlayer.Services.AlbumArt;

public interface IAlbumArtProviderManager
{
    IReadOnlyList<IAlbumArtProvider> Providers { get; }
}
