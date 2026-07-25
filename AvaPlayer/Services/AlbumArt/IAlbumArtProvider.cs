using AvaPlayer.Models;

namespace AvaPlayer.Services.AlbumArt;

public interface IAlbumArtProvider
{
    string Name { get; }
    bool RequiresNetwork { get; }
    Task<byte[]?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default);
}
