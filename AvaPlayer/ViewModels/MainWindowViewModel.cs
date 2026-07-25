using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvaPlayer.Models;
using AvaPlayer.Services.PlaybackSession;
using AvaPlayer.Services.Playlist;

namespace AvaPlayer.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IPlaylistService _playlistService;
    private readonly IPlaybackSessionClient _sessionClient;
    private bool _isInitialized;
    private bool _windowEventsWired;
    private bool _settingsEventsWired;
    private bool _disposed;

    public MainWindowViewModel(
        PlayerBarViewModel playerBar,
        PlaylistViewModel playlist,
        SettingsViewModel settings,
        LyricsViewModel lyrics,
        IPlaylistService playlistService,
        IPlaybackSessionClient sessionClient)
    {
        PlayerBar = playerBar;
        Playlist = playlist;
        Settings = settings;
        Lyrics = lyrics;
        _playlistService = playlistService;
        _sessionClient = sessionClient;

        WireSettingsEvents();
        WireWindowEvents();
    }

    public PlayerBarViewModel PlayerBar { get; }

    public PlaylistViewModel Playlist { get; }

    public SettingsViewModel Settings { get; }

    public LyricsViewModel Lyrics { get; }

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
            await _sessionClient.RestoreTrackAsync(cancellationToken);
            await Settings.InitializeAsync(cancellationToken);
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

    private void WireSettingsEvents()
    {
        if (_settingsEventsWired) return;
        PlayerBar.PropertyChanged += OnPlayerBarPropertyChanged;
        _settingsEventsWired = true;
    }

    private void UnwireSettingsEvents()
    {
        if (!_settingsEventsWired) return;
        PlayerBar.PropertyChanged -= OnPlayerBarPropertyChanged;
        _settingsEventsWired = false;
    }

    private void OnPlayerBarPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.IsSettingsVisible))
        {
            Settings.IsSettingsVisible = PlayerBar.IsSettingsVisible;
        }
        else if (e.PropertyName == nameof(PlayerBarViewModel.BackgroundBrush))
        {
            Settings.BackgroundBrush = PlayerBar.BackgroundBrush;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnwireWindowEvents();
        UnwireSettingsEvents();
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
