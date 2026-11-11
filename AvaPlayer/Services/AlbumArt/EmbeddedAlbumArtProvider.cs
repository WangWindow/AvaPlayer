using AvaPlayer.Models;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.AlbumArt;

public sealed class EmbeddedAlbumArtProvider(ILogger<EmbeddedAlbumArtProvider> logger) : IAlbumArtProvider
{
    public string Name => "Embedded";
    public bool RequiresNetwork => false;

    public async Task<byte[]?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(() =>
            {
                using var tagFile = TagLib.File.Create(track.FilePath);
                var picture = tagFile.Tag.Pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover)
                    ?? tagFile.Tag.Pictures.FirstOrDefault();
                return picture?.Data.Data;
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "[AlbumArt:{Provider}] 读取内嵌封面失败: {Message}", Name, ex.Message);
            return null;
        }
    }
}
