using Avalonia.Media.Imaging;
using AvaPlayer.Models;

namespace AvaPlayer.Services.AlbumArt;

/// <summary>
/// Resolves album artwork for a track using local metadata, cache, and configured providers.
/// </summary>
public interface IAlbumArtService
{
    Task<Bitmap?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default);
}
