using AvaPlayer.Models;

namespace AvaPlayer.Services.Database;

public interface IDatabaseService
{
    string DatabasePath { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetLibraryFoldersAsync(CancellationToken cancellationToken = default);
    Task SaveLibraryFolderAsync(string folderPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlaylistInfo>> GetPlaylistsAsync(CancellationToken cancellationToken = default);
    Task SavePlaylistAsync(PlaylistInfo playlist, CancellationToken cancellationToken = default);
    Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken cancellationToken = default);
    /// <summary>
    /// Deletes the playlist row and all tracks assigned to it in a single transaction.
    /// Files on disk are not touched.
    /// </summary>
    Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Track>> GetTracksAsync(string? playlistId = null, CancellationToken cancellationToken = default);
    Task SaveTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default);
    Task DeleteTracksAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default);
    Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken = default);
    /// <summary>
    /// Saves multiple settings atomically within a single transaction.
    /// All keys are upserted; on conflict the existing value is replaced.
    /// </summary>
    Task SaveSettingsBatchAsync(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default);
    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default);
}
