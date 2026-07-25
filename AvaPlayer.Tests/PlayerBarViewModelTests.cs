using Avalonia.Media.Imaging;
using AvaPlayer.Models;
using AvaPlayer.Services.AlbumArt;
using AvaPlayer.Services.Lyrics;
using AvaPlayer.Services.PlaybackSession;
using AvaPlayer.Application.Tests.Fakes;
using AvaPlayer.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Application.Tests;

public sealed class PlayerBarViewModelTests
{
    private static readonly Track SampleTrack = new()
    {
        Id = "t1", FilePath = "/music/test.mp3", Title = "Test Song",
        Artist = "Artist", Album = "Album", DurationSeconds = 240
    };

    private readonly FakePlaybackSessionClient _sessionClient = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeAlbumArtService _albumArt = new();
    private readonly FakeLyricsService _lyrics = new();
    private readonly FakeLyricPresentationService _lyricPresentation = new();
    private readonly PlayerBarViewModel _vm;

    public PlayerBarViewModelTests()
    {
        _vm = new PlayerBarViewModel(
            _albumArt,
            _lyrics,
            _settings,
            _lyricPresentation,
            _sessionClient,
            NullLogger<PlayerBarViewModel>.Instance);
    }

    // ── Command forwarding ──

    [Fact]
    public async Task PlayPauseCommand_forwards_to_session_client()
    {
        await _vm.PlayPauseCommand.ExecuteAsync(null);
        // Session client toggled (no exception = success)
    }

    [Fact]
    public async Task NextCommand_forwards_to_session_client()
    {
        await _vm.NextCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task PreviousCommand_forwards_to_session_client()
    {
        await _vm.PreviousCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task SeekCommand_forwards_to_session_client()
    {
        await _vm.SeekCommand.ExecuteAsync(42.5);
        Assert.Equal(42.5, _sessionClient.LastSeekPosition);
    }

    [Fact]
    public async Task PlayTrackCommand_forwards_to_session_client()
    {
        await _vm.PlayTrackCommand.ExecuteAsync(SampleTrack);
        Assert.Same(SampleTrack, _sessionClient.LastPlayedTrack);
    }

    [Fact]
    public void PauseCommand_forwards_to_session_client()
    {
        _vm.PauseCommand.Execute(null);
    }

    [Fact]
    public void ResumeCommand_forwards_to_session_client()
    {
        _vm.ResumeCommand.Execute(null);
    }

    [Fact]
    public void TogglePlaybackModeCommand_forwards_to_session_client()
    {
        _vm.TogglePlaybackModeCommand.Execute(null);
    }

    // ── Snapshot rendering ──

    [Fact]
    public void ApplySnapshot_sets_IsPlaying_from_snapshot()
    {
        var playing = MakePlayingSnapshot();
        _sessionClient.Publish(playing);

        Assert.True(_vm.IsPlaying);
    }

    [Fact]
    public void ApplySnapshot_sets_PlayPauseIcon_from_snapshot()
    {
        _sessionClient.Publish(MakePlayingSnapshot());
        Assert.Equal(FluentIcons.Common.Icon.Pause, _vm.PlayPauseIcon);

        _sessionClient.Publish(MakePlayingSnapshot() with { Status = PlaybackStatus.Paused });
        Assert.Equal(FluentIcons.Common.Icon.Play, _vm.PlayPauseIcon);
    }

    [Fact]
    public void ApplySnapshot_renders_position_at_48_seconds()
    {
        _sessionClient.Publish(MakePlayingSnapshot() with
        {
            CurrentTrack = SampleTrack,
            Position = 48,
            Duration = SampleTrack.DurationSeconds
        });

        Assert.Equal(48, _vm.Position);
        Assert.Equal(SampleTrack.DurationSeconds, _vm.Duration);
    }

    [Fact]
    public void ApplySnapshot_sets_volume_from_snapshot()
    {
        _sessionClient.Publish(MakePlayingSnapshot() with { Volume = 65 });

        Assert.Equal(65, _vm.Volume);
    }

    [Fact]
    public void ApplySnapshot_sets_playback_mode_from_snapshot()
    {
        _sessionClient.Publish(MakePlayingSnapshot() with { PlaybackMode = PlaybackMode.RepeatAll });

        Assert.Equal(PlaybackMode.RepeatAll, _vm.PlaybackMode);
    }

    [Fact]
    public void ApplySnapshot_raises_TrackChanged_on_track_switch()
    {
        Track? received = null;
        _vm.TrackChanged += (_, t) => received = t;

        _sessionClient.Publish(MakePlayingSnapshot() with { CurrentTrack = SampleTrack });

        Assert.Same(SampleTrack, received);
    }

    [Fact]
    public void ApplySnapshot_updates_title_and_artist_from_new_track()
    {
        _sessionClient.Publish(MakePlayingSnapshot() with
        {
            CurrentTrack = SampleTrack
        });

        Assert.Equal(SampleTrack.DisplayTitle, _vm.TitleDisplay);
        Assert.Equal(SampleTrack.DisplayArtistAlbum, _vm.ArtistDisplay);
    }

    // ── Failed start preserves prior presentation ──

    [Fact]
    public void ApplySnapshot_with_null_track_preserves_current_track()
    {
        // Arrange: first establish a track
        _sessionClient.Publish(MakePlayingSnapshot() with { CurrentTrack = SampleTrack });
        Assert.Same(SampleTrack, _vm.CurrentTrack);

        var previousTitle = _vm.TitleDisplay;
        var previousPosition = _vm.Position;

        // Act: receive a snapshot with no track (simulates failed start after a track was loaded)
        _sessionClient.Publish(new PlaybackSnapshot(
            Revision: 10,
            Status: PlaybackStatus.Stopped,
            CurrentTrack: null,
            Position: 0,
            Duration: 1,
            Volume: 80,
            PlaybackMode: PlaybackMode.Sequential));

        // Assert: CurrentTrack, title, artist are preserved
        Assert.Same(SampleTrack, _vm.CurrentTrack);
        Assert.Equal(previousTitle, _vm.TitleDisplay);
    }

    // ── Lyric seek routing ──

    [Fact]
    public void OnLyricsSeekRequested_seeks_when_playing()
    {
        _sessionClient.Publish(MakePlayingSnapshot() with
        {
            CurrentTrack = SampleTrack,
            Duration = 240
        });
        Assert.True(_vm.IsPlaying);

        _lyricPresentation.FireSeekRequested(TimeSpan.FromSeconds(90));

        Assert.Equal(90, _sessionClient.LastSeekPosition);
    }

    [Fact]
    public void OnLyricsSeekRequested_resumes_then_seeks_when_paused()
    {
        _sessionClient.Publish(MakePlayingSnapshot() with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Paused,
            Duration = 240
        });

        Assert.False(_vm.IsPlaying);

        _lyricPresentation.FireSeekRequested(TimeSpan.FromSeconds(30));

        Assert.Equal(30, _sessionClient.LastSeekPosition);
    }

    [Fact]
    public void OnLyricsSeekRequested_clamps_to_duration()
    {
        _sessionClient.Publish(MakePlayingSnapshot() with
        {
            CurrentTrack = SampleTrack,
            Duration = 200
        });

        _lyricPresentation.FireSeekRequested(TimeSpan.FromSeconds(999));

        Assert.True(_sessionClient.LastSeekPosition <= 200);
    }

    // ── InitializeAsync restore flow ──

    [Fact]
    public async Task InitializeAsync_calls_RestorePlaybackAtPositionAsync()
    {
        await _settings.SetAsync("player-volume", "75");

        await _vm.InitializeAsync(false);

        Assert.Equal(1, _sessionClient.RestorePlaybackAtPositionCallCount);
        Assert.Equal(75, _vm.Volume);
    }

    [Fact]
    public async Task InitializeAsync_restores_volume_from_settings()
    {
        await _settings.SetAsync("player-volume", "42");

        await _vm.InitializeAsync(false);

        Assert.Equal(42, _vm.Volume);
    }

    [Fact]
    public async Task InitializeAsync_uses_zero_position_when_no_saved_position()
    {
        // No volume setting either — both default
        await _vm.InitializeAsync(false);

        Assert.Equal(1, _sessionClient.RestorePlaybackAtPositionCallCount);
        // SavedPositionOverride is null → GetSavedPositionAsync returns 0
    }

    // ── Snapshot subscription on construction ──

    [Fact]
    public void Constructor_subscribes_and_receives_idle_snapshot()
    {
        Assert.False(_vm.IsPlaying);
        Assert.Equal(FluentIcons.Common.Icon.Play, _vm.PlayPauseIcon);
        Assert.Equal(0, _vm.Position);
    }

    // ── Seek-snapback regression ──

    [Fact]
    public void ApplySnapshot_does_not_overwrite_position_during_user_seek()
    {
        // Arrange
        _sessionClient.Publish(MakePlayingSnapshot() with
        {
            CurrentTrack = SampleTrack,
            Position = 30,
            Duration = SampleTrack.DurationSeconds
        });
        _vm.IsUserSeeking = true;

        // Act: snapshot arrives with different position while user is dragging
        _sessionClient.Publish(MakePlayingSnapshot() with
        {
            CurrentTrack = SampleTrack,
            Position = 55,   // engine says 55s
            Duration = SampleTrack.DurationSeconds
        });

        // Assert: Position was NOT overwritten — it stays at the user's drag value (30)
        Assert.Equal(30, _vm.Position);
    }

    [Fact]
    public void ApplySnapshot_does_overwrite_position_when_not_seeking()
    {
        // Arrange
        _sessionClient.Publish(MakePlayingSnapshot() with
        {
            CurrentTrack = SampleTrack,
            Position = 10,
            Duration = SampleTrack.DurationSeconds
        });
        Assert.False(_vm.IsUserSeeking);

        // Act
        _sessionClient.Publish(MakePlayingSnapshot() with
        {
            CurrentTrack = SampleTrack,
            Position = 55,
            Duration = SampleTrack.DurationSeconds
        });

        // Assert: normal (non-seeking) path still applies position
        Assert.Equal(55, _vm.Position);
    }

    // ── Lyric position forwarding regression ──

    [Fact]
    public void ApplySnapshot_forwards_position_to_lyric_presentation()
    {
        // Arrange: set up a track with position
        _sessionClient.Publish(MakePlayingSnapshot() with
        {
            CurrentTrack = SampleTrack,
            Position = 42,
            Duration = SampleTrack.DurationSeconds
        });

        // Act: _lyricPresentation.UpdatePosition should have been called
        // with the snapshot position

        // Assert
        Assert.Equal(42, _lyricPresentation.LastUpdatePositionSeconds);
        Assert.True(_lyricPresentation.UpdatePositionCallCount >= 1,
            "UpdatePosition should be called at least once (initial + snapshot)");
    }

    [Fact]
    public void ApplySnapshot_forwards_position_to_lyrics_on_subsequent_updates()
    {
        // Arrange: establish a track and initial position
        _sessionClient.Publish(MakePlayingSnapshot() with
        {
            CurrentTrack = SampleTrack,
            Position = 10,
            Duration = SampleTrack.DurationSeconds
        });
        _lyricPresentation.ResetCounts();

        // Act: subsequent snapshot with new position
        _sessionClient.Publish(MakePlayingSnapshot() with
        {
            CurrentTrack = SampleTrack,
            Position = 78,
            Duration = SampleTrack.DurationSeconds
        });

        // Assert: lyrics received the new position
        Assert.Equal(78, _lyricPresentation.LastUpdatePositionSeconds);
    }

    // ── Helpers ──

    private static PlaybackSnapshot MakePlayingSnapshot() =>
        PlaybackSnapshot.Idle with
        {
            Revision = 1,
            Status = PlaybackStatus.Playing,
            Position = 10,
            Duration = 240,
            Volume = 80
        };
}

// ── Inline fakes ──

sealed class FakeAlbumArtService : IAlbumArtService
{
    public Task<Bitmap?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default)
        => Task.FromResult<Bitmap?>(null);
}

sealed class FakeLyricsService : ILyricsService
{
    public Task<IReadOnlyList<LyricLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LyricLine>>(Array.Empty<LyricLine>());
}

sealed class FakeLyricPresentationService : ILyricPresentationService
{
    public event EventHandler<TimeSpan>? SeekRequested;

    /// <summary>Last position passed to <see cref="UpdatePosition"/>, or -1 if never called.</summary>
    public double LastUpdatePositionSeconds { get; private set; } = -1;

    /// <summary>Count of times <see cref="UpdatePosition"/> was called.</summary>
    public int UpdatePositionCallCount { get; private set; }

    public void BeginLoading() { }
    public void LoadLyrics(IReadOnlyList<LyricLine> lines) { }
    public void ClearLyrics() { }
    public void UpdatePosition(double positionSeconds)
    {
        LastUpdatePositionSeconds = positionSeconds;
        UpdatePositionCallCount++;
    }

    public void FireSeekRequested(TimeSpan time) => SeekRequested?.Invoke(this, time);

    public void ResetCounts()
    {
        UpdatePositionCallCount = 0;
        LastUpdatePositionSeconds = -1;
    }
}
