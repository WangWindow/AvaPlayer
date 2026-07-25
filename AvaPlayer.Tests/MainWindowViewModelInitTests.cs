using System.ComponentModel;
using Avalonia.Media.Imaging;
using AvaPlayer.Models;
using AvaPlayer.Services.AlbumArt;
using AvaPlayer.Services.Lyrics;
using AvaPlayer.Services.Network;
using AvaPlayer.Services.PlaybackSession;
using AvaPlayer.Services.Playlist;
using AvaPlayer.Services.Settings;
using AvaPlayer.Application.Tests.Fakes;
using AvaPlayer.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Application.Tests;

/// <summary>
/// Tests for <see cref="MainWindowViewModel.InitializeAsync"/> contract,
/// especially the lightweight-mode (hydrateVisuals: false) path.
/// </summary>
public sealed class MainWindowViewModelInitTests
{
    [Fact]
    public async Task InitializeAsync_hydrateVisuals_false_loads_playlist_and_restores_track()
    {
        var playlist = new FakePlaylistService();
        var session = new FakePlaybackSessionClient();
        var vm = CreateViewModel(playlist, session);

        await vm.InitializeAsync(hydrateVisuals: false);

        Assert.Equal(1, playlist.LoadCallCount);
        Assert.Equal(1, session.RestoreTrackCallCount);
    }

    [Fact]
    public async Task InitializeAsync_hydrateVisuals_true_loads_playlist_and_restores_track()
    {
        var playlist = new FakePlaylistService();
        var session = new FakePlaybackSessionClient();
        var vm = CreateViewModel(playlist, session);

        await vm.InitializeAsync(hydrateVisuals: true);

        Assert.Equal(1, playlist.LoadCallCount);
        Assert.Equal(1, session.RestoreTrackCallCount);
    }

    [Fact]
    public async Task InitializeAsync_hydrateVisuals_false_sets_isInitialized()
    {
        var playlist = new FakePlaylistService();
        var session = new FakePlaybackSessionClient();
        var vm = CreateViewModel(playlist, session);

        await vm.InitializeAsync(hydrateVisuals: false);

        // Second call should be idempotent (no extra LoadAsync)
        await vm.InitializeAsync(hydrateVisuals: false);

        Assert.Equal(1, playlist.LoadCallCount);
        Assert.Equal(1, session.RestoreTrackCallCount);
    }

    [Fact]
    public async Task InitializeAsync_lightweight_then_window_does_not_duplicate_load()
    {
        var playlist = new FakePlaylistService();
        var session = new FakePlaybackSessionClient();
        var vm = CreateViewModel(playlist, session);

        // Lightweight init first
        await vm.InitializeAsync(hydrateVisuals: false);
        Assert.Equal(1, playlist.LoadCallCount);

        // Window init later should not reload
        await vm.InitializeAsync(hydrateVisuals: true);
        Assert.Equal(1, playlist.LoadCallCount);
    }

    private static MainWindowViewModel CreateViewModel(IPlaylistService playlist, IPlaybackSessionClient session)
    {
        var settings = new FakeSettingsService();
        var positionMemory = new FakePlaybackPositionMemoryService();
        var albumArt = new InitTestAlbumArtService();
        var lyricsService = new InitTestLyricsService();
        var lyricPresentation = new InitTestLyricPresentationService();
        var networkAccess = new InitTestNetworkAccessService();
        var lyricPreferences = new InitTestLyricPreferencesService();

        var playerBar = new PlayerBarViewModel(
            albumArt, lyricsService, settings, lyricPresentation, session,
            NullLogger<PlayerBarViewModel>.Instance);
        var playlistVm = new PlaylistViewModel(playlist, NullLogger<PlaylistViewModel>.Instance);
        var settingsVm = new SettingsViewModel(settings, networkAccess, lyricPreferences, positionMemory);
        var lyricsVm = new LyricsViewModel(lyricPreferences, NullLogger<LyricsViewModel>.Instance);

        return new MainWindowViewModel(playerBar, playlistVm, settingsVm, lyricsVm, playlist, session);
    }
}

// ── Inline fakes (unique names to avoid collision with PlayerBarViewModelTests) ──

sealed class InitTestAlbumArtService : IAlbumArtService
{
    public Task<Bitmap?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default)
        => Task.FromResult<Bitmap?>(null);
}

sealed class InitTestLyricsService : ILyricsService
{
    public Task<IReadOnlyList<LyricLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LyricLine>>(Array.Empty<LyricLine>());
}

sealed class InitTestLyricPresentationService : ILyricPresentationService
{
    public event EventHandler<TimeSpan>? SeekRequested;

    public void BeginLoading() { }
    public void LoadLyrics(IReadOnlyList<LyricLine> lines) { }
    public void ClearLyrics() { }
    public void UpdatePosition(double positionSeconds) { }
}

sealed class InitTestNetworkAccessService : INetworkAccessService
{
    public bool IsNetworkEnabled { get; set; } = true;
    public bool IsEnabled => IsNetworkEnabled;
    public event EventHandler<bool>? NetworkAccessChanged;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task PersistAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

sealed class InitTestLyricPreferencesService : ILyricPreferencesService
{
    public LyricFontPreset FontPreset { get; set; } = LyricFontPreset.Medium;
    public bool IsAutoCenterEnabled { get; set; } = true;
    public bool IsLyricClickSeekEnabled { get; set; } = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
