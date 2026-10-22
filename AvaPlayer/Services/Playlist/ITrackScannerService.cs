using AvaPlayer.Models;

namespace AvaPlayer.Services.Playlist;

public interface ITrackScannerService
{
    Task<IReadOnlyList<Track>> ScanFolderAsync(string folderPath, bool includeSubfolders = true, CancellationToken cancellationToken = default);
}
