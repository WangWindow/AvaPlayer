using AvaPlayer.Models;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.AlbumArt;

public sealed class EmbeddedAlbumArtProvider : IAlbumArtProvider
{
    private readonly ILogger<EmbeddedAlbumArtProvider> _logger;

    public EmbeddedAlbumArtProvider(ILogger<EmbeddedAlbumArtProvider> logger)
    {
        _logger = logger;
    }

    public string Name => "Embedded";
    public bool RequiresNetwork => false;

    public Task<byte[]?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default)
    {
        try
        {
            using var tagFile = TagLib.File.Create(track.FilePath);
            return Task.FromResult<byte[]?>(tagFile.Tag.Pictures.FirstOrDefault()?.Data.Data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[AlbumArt:{Provider}] 读取内嵌封面失败: {Message}", Name, ex.Message);
            return Task.FromResult<byte[]?>(null);
        }
    }
}
