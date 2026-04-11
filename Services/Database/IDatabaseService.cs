using AvaPlayer.Models;

namespace AvaPlayer.Services.Database;

public interface IDatabaseService
{
    string DatabasePath { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetLibraryFoldersAsync(CancellationToken cancellationToken = default);
    Task SaveLibraryFolderAsync(string folderPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetTracksAsync(CancellationToken cancellationToken = default);
    Task SaveTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default);
    Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default);
}
