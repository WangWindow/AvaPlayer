using AvaPlayer.Models;

namespace AvaPlayer.Services.Playlist;

public interface ITrackScannerService
{
    Task<IReadOnlyList<Track>> ScanFolderAsync(string folderPath, CancellationToken cancellationToken = default);
}
