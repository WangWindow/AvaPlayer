using AvaPlayer.Models;
using AvaPlayer.Services.PlaybackSession;
using AvaPlayer.Application.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Application.Tests;

/// <summary>
/// Integration-style tests for persistence semantics of the real <see cref="PlaybackSession"/>
/// with hand-written fakes. Verifies atomic batch writes, corrupt position fallback,
/// and checkpoint lifecycle.
/// </summary>
public sealed class PlaybackSessionPersistenceIntegrationTests : IDisposable
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

    public PlaybackSessionPersistenceIntegrationTests()
    {
        _session = new PlaybackSession(
            _player,
            _playlist,
            _settings,
            _positionMemory,
            NullLogger<PlaybackSession>.Instance);
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    // ── Atomic batch ──

    [Fact]
    public async Task PersistAsync_writes_all_keys_in_single_batch()
    {
        await _session.PlayTrackAsync(SampleTrack);
        // Allow command loop to process PlayTrackAsync
        await Task.Delay(100);

        // Reset batch count (PlayTrackAsync may trigger checkpoint on first tick)
        var beforeCount = _settings.BatchCallCount;

        await _session.PersistAsync();

        Assert.Equal(beforeCount + 1, _settings.BatchCallCount);
        Assert.Single(_settings.BatchCalls);

        var lastBatch = _settings.BatchCalls[^1];
        Assert.Equal(3, lastBatch.Count);
        Assert.Equal(SampleTrack.FilePath, lastBatch["current-track-path"]);
        Assert.True(lastBatch.ContainsKey("playback-position-seconds"));
        Assert.True(lastBatch.ContainsKey("player-volume"));
    }

    [Fact]
    public async Task PersistAsync_skips_position_when_memory_disabled()
    {
        _positionMemory.IsEnabled = false;
        await _session.PlayTrackAsync(SampleTrack);
        await Task.Delay(100);

        var beforeCount = _settings.BatchCallCount;
        await _session.PersistAsync();

        Assert.Equal(beforeCount + 1, _settings.BatchCallCount);
        var lastBatch = _settings.BatchCalls[^1];
        Assert.Equal(2, lastBatch.Count);
        Assert.Equal(SampleTrack.FilePath, lastBatch["current-track-path"]);
        Assert.True(lastBatch.ContainsKey("player-volume"));
    }

    [Fact]
    public async Task PersistAsync_noop_when_no_track()
    {
        var beforeCount = _settings.BatchCallCount;
        await _session.PersistAsync();
        Assert.Equal(beforeCount, _settings.BatchCallCount);
    }

    // ── Corrupt/missing position ──

    [Fact]
    public async Task GetSavedPositionAsync_returns_zero_for_missing_key()
    {
        var pos = await _session.GetSavedPositionAsync();
        Assert.Equal(0, pos);
    }

    [Fact]
    public async Task GetSavedPositionAsync_returns_zero_for_corrupt_value()
    {
        await _settings.SetAsync("playback-position-seconds", "not-a-number");
        var pos = await _session.GetSavedPositionAsync();
        Assert.Equal(0, pos);
    }

    [Fact]
    public async Task GetSavedPositionAsync_returns_zero_for_empty_value()
    {
        await _settings.SetAsync("playback-position-seconds", "");
        var pos = await _session.GetSavedPositionAsync();
        Assert.Equal(0, pos);
    }

    [Fact]
    public async Task GetSavedPositionAsync_returns_valid_position()
    {
        await _settings.SetAsync("playback-position-seconds", "42.500000");
        var pos = await _session.GetSavedPositionAsync();
        Assert.Equal(42.5, pos);
    }

    [Fact]
    public async Task GetSavedPositionAsync_clamps_negative_to_zero()
    {
        await _settings.SetAsync("playback-position-seconds", "-5.000000");
        var pos = await _session.GetSavedPositionAsync();
        Assert.Equal(0, pos);
    }

    // ── Volume persistence ──

    [Fact]
    public async Task PersistAsync_writes_volume_from_snapshot()
    {
        await _session.PlayTrackAsync(SampleTrack);
        await Task.Delay(100);

        // Set a non-default volume through the session command queue
        await _session.SetVolumeAsync(42);

        var beforeCount = _settings.BatchCallCount;
        await _session.PersistAsync();

        Assert.Equal(beforeCount + 1, _settings.BatchCallCount);
        var lastBatch = _settings.BatchCalls[^1];
        Assert.True(lastBatch.ContainsKey("player-volume"));
        Assert.Equal("42", lastBatch["player-volume"]);
    }

    [Fact]
    public async Task PersistAsync_writes_default_volume_when_not_changed()
    {
        await _session.PlayTrackAsync(SampleTrack);
        await Task.Delay(100);

        // Default volume from Idle snapshot is 80 — do not change it
        var beforeCount = _settings.BatchCallCount;
        await _session.PersistAsync();

        Assert.Equal(beforeCount + 1, _settings.BatchCallCount);
        var lastBatch = _settings.BatchCalls[^1];
        Assert.True(lastBatch.ContainsKey("player-volume"));
        Assert.Equal("80", lastBatch["player-volume"]);
    }

    // ── Checkpoint lifecycle ──

    [Fact]
    public async Task Checkpoint_loop_starts_without_error()
    {
        // The checkpoint starts in the constructor. Just verify the session
        // is operational after creation (no crash from the background loop).
        Assert.NotNull(_session.LatestSnapshot);
        Assert.Equal(PlaybackStatus.Stopped, _session.LatestSnapshot.Status);
    }

    [Fact]
    public async Task Checkpoint_disposal_does_not_throw()
    {
        await _session.PlayTrackAsync(SampleTrack);
        await Task.Delay(100);

        // Dispose the session; the checkpoint loop should cancel cleanly
        _session.Dispose();

        // After disposal, the session should not crash on further operations
        Assert.NotNull(_session.LatestSnapshot);
    }

    [Fact]
    public async Task Double_dispose_is_safe()
    {
        _session.Dispose();
        // Second dispose must not throw
        _session.Dispose();
    }

    [Fact]
    public async Task Checkpoint_skips_write_when_no_track()
    {
        // Session starts with no track (Idle). The checkpoint loop runs
        // but should skip writes because LatestSnapshot.HasTrack is false.
        // Wait a short moment for any potential checkpoint tick, then verify
        // no batch calls were made (allow for the initial checkpoint delay
        // which is 30s and won't fire in this timeframe).
        await Task.Delay(200);
        Assert.Equal(0, _settings.BatchCallCount);
    }

    [Fact]
    public async Task PersistAsync_after_checkpoint_is_independent()
    {
        // Start a track, let the session run, then explicitly persist.
        // Verify the explicit persist call is counted separately from
        // any checkpoint calls.
        await _session.PlayTrackAsync(SampleTrack);
        await Task.Delay(100);

        var beforeExplicit = _settings.BatchCallCount;
        await _session.PersistAsync();
        Assert.Equal(beforeExplicit + 1, _settings.BatchCallCount);
    }

    // ── RestoreTrackAsync ──

    [Fact]
    public async Task RestoreTrackAsync_sets_current_track_from_persisted_path()
    {
        _playlist.AddTrack(SampleTrack);
        await _settings.SetAsync("current-track-path", SampleTrack.FilePath);

        await _session.RestoreTrackAsync();

        Assert.Same(SampleTrack, _playlist.CurrentTrack);
    }

    [Fact]
    public async Task RestoreTrackAsync_selects_first_track_when_no_path_persisted()
    {
        var track1 = new Track { Id = "t1", FilePath = "/music/a.mp3", Title = "A" };
        var track2 = new Track { Id = "t2", FilePath = "/music/b.mp3", Title = "B" };
        _playlist.AddTrack(track1);
        _playlist.AddTrack(track2);

        await _session.RestoreTrackAsync();

        Assert.Same(track1, _playlist.CurrentTrack);
    }

    [Fact]
    public async Task RestoreTrackAsync_falls_back_to_first_track_when_path_unknown()
    {
        var track1 = new Track { Id = "t1", FilePath = "/music/a.mp3", Title = "A" };
        _playlist.AddTrack(track1);
        await _settings.SetAsync("current-track-path", "/music/nonexistent.mp3");

        await _session.RestoreTrackAsync();

        Assert.Same(track1, _playlist.CurrentTrack);
    }

    [Fact]
    public async Task RestoreTrackAsync_noop_when_queue_empty_and_no_path()
    {
        await _session.RestoreTrackAsync();
        Assert.Null(_playlist.CurrentTrack);
    }

    [Fact]
    public async Task RestoreTrackAsync_preserves_existing_current_track_when_no_path()
    {
        var track = new Track { Id = "t1", FilePath = "/music/a.mp3", Title = "A" };
        _playlist.AddTrack(track);
        _playlist.SetCurrentTrack(track);

        await _session.RestoreTrackAsync();

        Assert.Same(track, _playlist.CurrentTrack);
    }

    // ── Full lifecycle: play → seek → persist → new-session → restore → verify position ──

    [Fact]
    public async Task Full_lifecycle_play_seek_persist_restore_preserves_position()
    {
        // Session A: play, seek to known position, persist
        await _session.PlayTrackAsync(SampleTrack);
        await Task.Delay(100);
        await _session.SeekAsync(42.5);
        await Task.Delay(100);
        await _session.PersistAsync();

        // Capture persisted settings
        var savedPath = await _settings.GetAsync("current-track-path");
        var savedPos = await _settings.GetAsync("playback-position-seconds");

        Assert.Equal(SampleTrack.FilePath, savedPath);
        Assert.Equal("42.500000", savedPos);

        // Session B: simulate restart with a fresh session
        var restoredPlayer = new FakePlayerService();
        using var sessionB = new PlaybackSession(
            restoredPlayer,
            _playlist,
            _settings,
            new FakePlaybackPositionMemoryService { IsEnabled = true },
            NullLogger<PlaybackSession>.Instance);

        // Restore: set track from persisted path, then restore at saved position
        await sessionB.RestoreTrackAsync();
        var restoredPos = await sessionB.GetSavedPositionAsync();
        Assert.Equal(42.5, restoredPos, 4);

        await sessionB.RestorePlaybackAtPositionAsync(restoredPos);
        await Task.Delay(100);

        // Verify the restored snapshot has the correct position
        Assert.Equal(42.5, restoredPlayer.LastStartPosition, 4);
        Assert.Equal(42.5, restoredPlayer.PausedPosition, 4);
        var snap = sessionB.LatestSnapshot;
        Assert.Same(SampleTrack, snap.CurrentTrack);
        Assert.Equal(PlaybackStatus.Paused, snap.Status);
        Assert.Equal(42.5, snap.Position, 2);
    }

    [Fact]
    public async Task Full_lifecycle_zero_position_persists_and_restores()
    {
        await _session.PlayTrackAsync(SampleTrack);
        await Task.Delay(100);
        // Seek to zero (should still persist 0.000000)
        await _session.SeekAsync(0);
        await Task.Delay(100);
        await _session.PersistAsync();

        var savedPos = await _settings.GetAsync("playback-position-seconds");
        Assert.Equal("0.000000", savedPos);

        using var sessionB = new PlaybackSession(
            new FakePlayerService(),
            _playlist,
            _settings,
            new FakePlaybackPositionMemoryService { IsEnabled = true },
            NullLogger<PlaybackSession>.Instance);

        await sessionB.RestoreTrackAsync();
        var restoredPos = await sessionB.GetSavedPositionAsync();
        Assert.Equal(0, restoredPos, 4);

        await sessionB.RestorePlaybackAtPositionAsync(restoredPos);
        await Task.Delay(100);

        var snap = sessionB.LatestSnapshot;
        Assert.Equal(PlaybackStatus.Paused, snap.Status);
        Assert.Equal(0, snap.Position, 2);
    }

    [Fact]
    public async Task Full_lifecycle_restore_without_persisted_position_uses_zero()
    {
        // No saved position — start fresh
        using var sessionB = new PlaybackSession(
            new FakePlayerService(),
            _playlist,
            _settings,
            new FakePlaybackPositionMemoryService { IsEnabled = true },
            NullLogger<PlaybackSession>.Instance);

        var savedPos = await sessionB.GetSavedPositionAsync();
        Assert.Equal(0, savedPos);
    }
}
