using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvaPlayer.Models;
using AvaPlayer.Services.Playlist;

namespace AvaPlayer.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IPlaylistService _playlistService;
    private bool _isInitialized;
    private bool _windowEventsWired;

    public MainWindowViewModel(
        PlayerBarViewModel playerBar,
        PlaylistViewModel playlist,
        IPlaylistService playlistService)
    {
        PlayerBar = playerBar;
        Playlist = playlist;
        _playlistService = playlistService;

        WireWindowEvents();
    }

    public PlayerBarViewModel PlayerBar { get; }

    public PlaylistViewModel Playlist { get; }

    [ObservableProperty]
    public partial bool IsPlaylistVisible { get; set; }

    [RelayCommand]
    private void TogglePlaylist() => IsPlaylistVisible = !IsPlaylistVisible;

    [RelayCommand]
    private void ClosePlaylist() => IsPlaylistVisible = false;

    public async Task InitializeAsync(bool hydrateVisuals = true, CancellationToken cancellationToken = default)
    {
        if (!_isInitialized)
        {
            await _playlistService.LoadAsync(cancellationToken);
            await PlayerBar.InitializeAsync(hydrateVisuals, cancellationToken);
            _isInitialized = true;
        }
        else if (hydrateVisuals)
        {
            await PlayerBar.EnsureVisualHydrationAsync(cancellationToken);
        }
        else
        {
            PlayerBar.SuspendVisualHydration();
        }

        if (hydrateVisuals)
        {
            WireWindowEvents();
            Playlist.Activate();
            Playlist.MarkCurrentTrack(_playlistService.CurrentTrack);
        }
        else
        {
            Playlist.Deactivate();
            UnwireWindowEvents();
        }
    }

    public Task EnsureWindowStateAsync(CancellationToken cancellationToken = default) =>
        InitializeAsync(hydrateVisuals: true, cancellationToken);

    public void ReleaseWindowState()
    {
        IsPlaylistVisible = false;
        Playlist.Deactivate();
        PlayerBar.SuspendVisualHydration();
        UnwireWindowEvents();
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
            UnwireWindowEvents();
        }
    }

    private void WireWindowEvents()
    {
        if (_windowEventsWired)
        {
            return;
        }

        Playlist.TrackSelected += OnTrackSelected;
        PlayerBar.TrackChanged += OnTrackChanged;
        _windowEventsWired = true;
    }

    private void UnwireWindowEvents()
    {
        if (!_windowEventsWired)
        {
            return;
        }

        Playlist.TrackSelected -= OnTrackSelected;
        PlayerBar.TrackChanged -= OnTrackChanged;
        _windowEventsWired = false;
    }
}
