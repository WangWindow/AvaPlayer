using AvaPlayer.Models;
using AvaPlayer.Services.PlaybackSession;
using AvaPlayer.Application.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Application.Tests;

/// <summary>
/// Position contract tests using the real <see cref="PlaybackSession"/> with
/// <see cref="FakePlayerService"/>. Verifies that position is correctly propagated
/// from player events through to the session snapshot for all playback states:
/// playing, paused, seeked, stopped, and restored.
/// </summary>
public sealed class PlaybackSessionPositionContractTests : IDisposable
{
    private static readonly Track SampleTrack = new()
    {
        Id = "t1", FilePath = "/music/test.mp3", Title = "Test Song",
        Artist = "Artist", Album = "Album", DurationSeconds = 240
    };

    private readonly FakePlayerService _player = new();
    private readonly FakePlaylistService _playlist = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakePlaybackPositionMemoryService _positionMemory = new();
    private readonly PlaybackSession _session;

    public PlaybackSessionPositionContractTests()
    {
        _session = new PlaybackSession(
            _player,
            _playlist,
            _settings,
            _positionMemory,
            NullLogger<PlaybackSession>.Instance);
        _playlist.AddTrack(SampleTrack);
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    // ── Helper ──

    /// <summary>
    /// Waits for the command loop to process enqueued work.
    /// PlayTrackAsync enqueues via EnqueueWithResultAsync which already
    /// awaits completion, but subsequent snapshot state settles via
    /// synchronous event handlers. A brief delay ensures command-loop
    /// serialization is through.
    /// </summary>
    private static async Task SettleAsync()
    {
        await Task.Delay(100);
    }

    // ── Track loaded position ──

    [Fact]
    public async Task PlayTrackAsync_normal_start_sets_position_to_zero()
    {
        await _session.PlayTrackAsync(SampleTrack);
        await SettleAsync();

        var snap = _session.LatestSnapshot;
        Assert.Equal(PlaybackStatus.Playing, snap.Status);
        Assert.Equal(0, snap.Position);
        Assert.Same(SampleTrack, snap.CurrentTrack);
    }

    [Fact]
    public async Task StartPaused_with_nonzero_position_sets_paused_position_in_snapshot()
    {
        // Simulates the restore flow: PlayerBarViewModel calls _player.PlayAsync
        // directly with startPaused=true and startPositionSeconds, after
        // RestoreTrackAsync has set the playlist's current track.
        _playlist.SetCurrentTrack(SampleTrack);
        await _player.PlayAsync(
            SampleTrack.FilePath,
            startPaused: true,
            startPositionSeconds: 20.4);

        Assert.Equal(PlaybackStatus.Paused, _session.LatestSnapshot.Status);
        Assert.Equal(20.4, _session.LatestSnapshot.Position, 4);
    }

    [Fact]
    public async Task StartPaused_with_zero_position_shows_paused_at_start()
    {
        _playlist.SetCurrentTrack(SampleTrack);
        await _player.PlayAsync(
            SampleTrack.FilePath,
            startPaused: true,
            startPositionSeconds: 0);

        Assert.Equal(PlaybackStatus.Paused, _session.LatestSnapshot.Status);
        Assert.Equal(0, _session.LatestSnapshot.Position);
    }

    [Fact]
    public async Task RestorePlaybackAtPosition_preserves_nonzero_position_for_resume()
    {
        _playlist.SetCurrentTrack(SampleTrack);

        await _session.RestorePlaybackAtPositionAsync(20.4);
        await SettleAsync();

        Assert.Equal(20.4, _player.LastStartPosition, 4);
        Assert.Equal(20.4, _player.PausedPosition, 4);
        Assert.Equal(20.4, _player.Position, 4);
        Assert.Equal(PlaybackStatus.Paused, _session.LatestSnapshot.Status);

        await _session.ResumeAsync();
        await SettleAsync();

        Assert.Equal(PlaybackStatus.Playing, _session.LatestSnapshot.Status);
        Assert.Equal(20.4, _player.Position, 4);
    }

    // ── Pause position ──

    [Fact]
    public async Task Pause_preserves_current_position_in_snapshot()
    {
        await _session.PlayTrackAsync(SampleTrack);
        await SettleAsync();

        // Advance position via seek (FakePlayerService has no timer)
        _player.Seek(55);
        await SettleAsync();

        double posBeforePause = _session.LatestSnapshot.Position;
        Assert.Equal(55, posBeforePause, 4);

        await _session.PauseAsync();
        await SettleAsync();

        Assert.Equal(PlaybackStatus.Paused, _session.LatestSnapshot.Status);
        Assert.Equal(55, _session.LatestSnapshot.Position, 4);
    }

    [Fact]
    public async Task Pause_when_already_paused_does_not_change_position()
    {
        _playlist.SetCurrentTrack(SampleTrack);
        await _player.PlayAsync(
            SampleTrack.FilePath,
            startPaused: true,
            startPositionSeconds: 30);
        await SettleAsync();

        Assert.Equal(30, _session.LatestSnapshot.Position, 4);

        // Double pause
        await _session.PauseAsync();
        await SettleAsync();

        Assert.Equal(PlaybackStatus.Paused, _session.LatestSnapshot.Status);
        Assert.Equal(30, _session.LatestSnapshot.Position, 4);
    }

    // ── Seek position ──

    [Fact]
    public async Task Seek_while_playing_updates_position()
    {
        await _session.PlayTrackAsync(SampleTrack);
        await SettleAsync();

        await _session.SeekAsync(42.5);
        await SettleAsync();

        Assert.Equal(42.5, _session.LatestSnapshot.Position, 4);
        Assert.Equal(PlaybackStatus.Playing, _session.LatestSnapshot.Status);
    }

    [Fact]
    public async Task Seek_while_paused_updates_paused_position()
    {
        await _session.PlayTrackAsync(SampleTrack);
        await SettleAsync();

        await _session.PauseAsync();
        await SettleAsync();

        await _session.SeekAsync(25);
        await SettleAsync();

        Assert.Equal(PlaybackStatus.Paused, _session.LatestSnapshot.Status);
        Assert.Equal(25, _session.LatestSnapshot.Position, 4);
    }

    [Fact]
    public async Task Seek_to_zero_while_paused_sets_position_zero()
    {
        _playlist.SetCurrentTrack(SampleTrack);
        await _player.PlayAsync(
            SampleTrack.FilePath,
            startPaused: true,
            startPositionSeconds: 50);
        await SettleAsync();

        await _session.SeekAsync(0);
        await SettleAsync();

        Assert.Equal(PlaybackStatus.Paused, _session.LatestSnapshot.Status);
        Assert.Equal(0, _session.LatestSnapshot.Position);
    }

    // ── Resume position ──

    [Fact]
    public async Task Resume_continues_from_paused_position()
    {
        await _session.PlayTrackAsync(SampleTrack);
        await SettleAsync();

        _player.Seek(33);
        await SettleAsync();

        await _session.PauseAsync();
        await SettleAsync();

        double pausedPos = _session.LatestSnapshot.Position;
        Assert.Equal(33, pausedPos, 4);

        await _session.ResumeAsync();
        await SettleAsync();

        // Position must NOT reset to 0 on resume
        Assert.Equal(PlaybackStatus.Playing, _session.LatestSnapshot.Status);
        Assert.Equal(33, _session.LatestSnapshot.Position, 4);
    }

    // ── Stop position ──

    [Fact]
    public async Task Stop_resets_position_to_zero()
    {
        await _session.PlayTrackAsync(SampleTrack);
        await SettleAsync();

        _player.Seek(77);
        await SettleAsync();

        _player.Stop();
        await SettleAsync();

        Assert.Equal(0, _session.LatestSnapshot.Position);
        // Stop fires PlaybackStateChanged(false) → session maps to Paused
        Assert.Equal(PlaybackStatus.Paused, _session.LatestSnapshot.Status);
    }

    // ── Failed/corrupt start ──

    [Fact]
    public async Task Failed_PlayTrackAsync_does_not_publish_track_loaded()
    {
        _player.PlayAsyncError = (path, ct) =>
            new InvalidOperationException("Engine failure");

        var result = await _session.PlayTrackAsync(SampleTrack);

        var failed = Assert.IsType<PlaybackStartResult.Failed>(result);
        Assert.True(result.IsFailure);
        Assert.Equal(PlaybackStartFailureKind.LoadFailed, failed.Kind);

        // Snapshot must not have changed — stays Idle
        Assert.False(_session.LatestSnapshot.HasTrack);
        Assert.Equal(PlaybackStatus.Stopped, _session.LatestSnapshot.Status);
    }

    [Fact]
    public async Task Failed_PlayTrackAsync_preserves_idle_snapshot()
    {
        var idleSnap = _session.LatestSnapshot;

        _player.PlayAsyncError = (path, ct) =>
            new InvalidOperationException("Engine failure");

        await _session.PlayTrackAsync(SampleTrack);

        Assert.Same(idleSnap, _session.LatestSnapshot);
        Assert.Equal(0, _session.LatestSnapshot.Revision);
    }

    // ── PositionChanged event propagation ──

    [Fact]
    public async Task PositionChanged_event_updates_snapshot_position()
    {
        await _session.PlayTrackAsync(SampleTrack);
        await SettleAsync();

        // Fire PositionChanged via helper (simulates timer tick in real player)
        _player.Position = 88.8;
        _player.FirePositionChanged();
        await SettleAsync();

        Assert.Equal(88.8, _session.LatestSnapshot.Position, 4);
    }

    [Fact]
    public async Task PositionChanged_event_while_paused_updates_snapshot_position()
    {
        _playlist.SetCurrentTrack(SampleTrack);
        await _player.PlayAsync(
            SampleTrack.FilePath,
            startPaused: true,
            startPositionSeconds: 15);
        await SettleAsync();

        Assert.Equal(15, _session.LatestSnapshot.Position, 4);

        // Simulate a PositionChanged with the paused cursor value
        // (matching the MiniAudioPlayerService fix: timer uses _pausedCursor, not source.Cursor)
        _player.FirePositionChanged();
        await SettleAsync();

        // Position must NOT revert to 0
        Assert.Equal(15, _session.LatestSnapshot.Position, 4);
    }

    [Fact]
    public async Task Seek_while_paused_does_not_fire_PositionChanged_with_zero()
    {
        _playlist.SetCurrentTrack(SampleTrack);
        await _player.PlayAsync(
            SampleTrack.FilePath,
            startPaused: true,
            startPositionSeconds: 10);
        await SettleAsync();

        // Seek while paused
        await _session.SeekAsync(20);
        await SettleAsync();

        // After seek, fire PositionChanged again (as the real timer would)
        _player.FirePositionChanged();
        await SettleAsync();

        Assert.Equal(20, _session.LatestSnapshot.Position, 4);
        Assert.Equal(PlaybackStatus.Paused, _session.LatestSnapshot.Status);
    }
}
