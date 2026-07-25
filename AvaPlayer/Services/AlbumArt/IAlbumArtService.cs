using Avalonia.Media.Imaging;
using AvaPlayer.Models;

namespace AvaPlayer.Services.AlbumArt;

public interface IAlbumArtService
{
    Task<Bitmap?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default);
}
