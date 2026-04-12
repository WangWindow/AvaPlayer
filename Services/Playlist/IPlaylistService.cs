using System.Collections.ObjectModel;
using AvaPlayer.Models;

namespace AvaPlayer.Services.Playlist;

public interface IPlaylistService
{
    ObservableCollection<Track> Queue { get; }
    Track? CurrentTrack { get; }
    PlaybackMode PlaybackMode { get; set; }

    Task LoadAsync(CancellationToken cancellationToken = default);
    Task AddFolderAsync(string folderPath, CancellationToken cancellationToken = default);
    Task RemoveTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default);
    void SetCurrentTrack(Track track);
    Track? GetNextTrack();
    Track? GetPreviousTrack();
}
