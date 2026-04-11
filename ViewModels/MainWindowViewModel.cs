using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvaPlayer.Models;
using AvaPlayer.Services.Playlist;

namespace AvaPlayer.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IPlaylistService _playlistService;

    public MainWindowViewModel(
        PlayerBarViewModel playerBar,
        PlaylistViewModel playlist,
        IPlaylistService playlistService)
    {
        PlayerBar = playerBar;
        Playlist = playlist;
        _playlistService = playlistService;

        Playlist.TrackSelected += OnTrackSelected;
        PlayerBar.TrackChanged += OnTrackChanged;
    }

    public PlayerBarViewModel PlayerBar { get; }

    public PlaylistViewModel Playlist { get; }

    [ObservableProperty]
    private bool _isPlaylistVisible;

    [RelayCommand]
    private void TogglePlaylist() => IsPlaylistVisible = !IsPlaylistVisible;

    [RelayCommand]
    private void ClosePlaylist() => IsPlaylistVisible = false;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _playlistService.LoadAsync(cancellationToken);
        Playlist.MarkCurrentTrack(_playlistService.CurrentTrack);
    }

    private void OnTrackSelected(object? sender, Track track)
    {
        IsPlaylistVisible = false;
        _ = PlayerBar.PlayTrackCommand.ExecuteAsync(track);
    }

    private void OnTrackChanged(object? sender, Track? track)
    {
        Playlist.MarkCurrentTrack(track);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Playlist.TrackSelected -= OnTrackSelected;
            PlayerBar.TrackChanged -= OnTrackChanged;
        }
    }
}
