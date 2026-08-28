using System.Collections.ObjectModel;
using AvaPlayer.Models;

namespace AvaPlayer.Services.Playlist;

public interface IPlaylistService
{
    ObservableCollection<Track> Queue { get; }
    ObservableCollection<PlaylistInfo> Playlists { get; }
    PlaylistInfo? SelectedPlaylist { get; }
    event EventHandler? SelectedPlaylistChanged;
    Track? CurrentTrack { get; }
    PlaybackMode PlaybackMode { get; set; }

    Task LoadAsync(CancellationToken cancellationToken = default);
    Task AddPlaylistAsync(string name, string folderPath, CancellationToken cancellationToken = default);
    Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken cancellationToken = default);
    Task RemovePlaylistAsync(string playlistId, CancellationToken cancellationToken = default);
    Task SelectPlaylistAsync(string? playlistId, CancellationToken cancellationToken = default);
    Task RemoveTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default);
    void SetCurrentTrack(Track track);
    Track? GetNextTrack();
    Track? GetPreviousTrack();
}
