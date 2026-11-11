using System.Collections.ObjectModel;
using AvaPlayer.Models;
using AvaPlayer.Services.Database;

namespace AvaPlayer.Services.Playlist;

/// <summary>
/// Owns the library's playlist catalog, selected track queue, and navigation state.
/// </summary>
/// <remarks>
/// Persistence and folder enumeration are delegated to <see cref="IDatabaseService"/>
/// and <see cref="ITrackScannerService"/> implementations.
/// </remarks>
public interface IPlaylistService
{
    ObservableCollection<Track> Queue { get; }
    ObservableCollection<PlaylistInfo> Playlists { get; }
    PlaylistInfo? SelectedPlaylist { get; }
    event EventHandler? SelectedPlaylistChanged;
    Track? CurrentTrack { get; }
    PlaybackMode PlaybackMode { get; set; }

    Task LoadAsync(CancellationToken cancellationToken = default);
    Task AddPlaylistAsync(string name, string folderPath, bool includeSubfolders = true, CancellationToken cancellationToken = default);
    Task RescanPlaylistAsync(string playlistId, bool includeSubfolders, CancellationToken cancellationToken = default);
    Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken cancellationToken = default);
    Task RemovePlaylistAsync(string playlistId, CancellationToken cancellationToken = default);
    Task SelectPlaylistAsync(string? playlistId, CancellationToken cancellationToken = default);
    Task RemoveTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default);
    void SetCurrentTrack(Track track);
    Track? GetNextTrack();
    Track? GetPreviousTrack();
}
