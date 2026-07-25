using AvaPlayer.Models;
using AvaPlayer.Services.PlaybackSession;
using AvaPlayer.Application.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Application.Tests;

/// <summary>
/// Tests for auto-advance, Next/Previous commands, and failure-preservation
/// behavior using the real <see cref="PlaybackSession"/> with hand-written fakes.
/// </summary>
public sealed class PlaybackSessionAutoAdvanceTests : IDisposable
{
    private static readonly Track Track1 = new()
    {
        Id = "t1", FilePath = "/music/1.mp3", Title = "Song One",
        Artist = "Artist", Album = "Album", DurationSeconds = 200
    };

    private static readonly Track Track2 = new()
    {
        Id = "t2", FilePath = "/music/2.mp3", Title = "Song Two",
        Artist = "Artist", Album = "Album", DurationSeconds = 180
    };

    private static readonly Track Track3 = new()
    {
        Id = "t3", FilePath = "/music/3.mp3", Title = "Song Three",
        Artist = "Artist", Album = "Album", DurationSeconds = 220
    };

    private readonly FakePlayerService _player = new();
    private readonly FakePlaylistService _playlist = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakePlaybackPositionMemoryService _positionMemory = new();
    private readonly PlaybackSession _session;

    public PlaybackSessionAutoAdvanceTests()
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

    // ── Helper ──

    /// <summary>
    /// Waits for a snapshot matching the predicate, with a timeout.
    /// </summary>
    private async Task<PlaybackSnapshot> AwaitSnapshotAsync(
        Func<PlaybackSnapshot, bool> predicate, TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(3);
        var tcs = new TaskCompletionSource<PlaybackSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var sub = _session.Subscribe(s =>
        {
            if (predicate(s))
                tcs.TrySetResult(s);
        });

        // Check if already matched (e.g. synchronous publish)
        if (predicate(_session.LatestSnapshot))
            return _session.LatestSnapshot;

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout.Value));
        if (completed == tcs.Task)
            return await tcs.Task;

        throw new TimeoutException(
            $"Timed out waiting for snapshot matching predicate after {timeout.Value.TotalSeconds}s. " +
            $"LatestSnapshot: {_session.LatestSnapshot.CurrentTrack?.Title ?? "null"} " +
            $"Status={_session.LatestSnapshot.Status}");
    }

    /// <summary>
    /// Starts playback of a track via the session, awaiting the Playing snapshot.
    /// </summary>
    private async Task PlayTrackAsync(Track track)
    {
        var result = await _session.PlayTrackAsync(track);
        Assert.IsType<PlaybackStartResult.Started>(result);
        Assert.True(result.IsSuccess);
    }

    // ── Auto-advance: TrackEnded event ──

    [Fact]
    public async Task TrackEnded_auto_advances_to_next_track_in_Sequential()
    {
        _playlist.AddTrack(Track1);
        _playlist.AddTrack(Track2);
        await PlayTrackAsync(Track1);

        Assert.Equal(Track1.Id, _session.LatestSnapshot.CurrentTrack!.Id);
        Assert.Equal(PlaybackStatus.Playing, _session.LatestSnapshot.Status);
        Assert.Equal(Track1.Id, _playlist.CurrentTrack!.Id);

        // Fire track-ended event (simulates natural track completion)
        _player.FireTrackEnded();

        var snap = await AwaitSnapshotAsync(s => s.CurrentTrack?.Id == Track2.Id);
        Assert.Equal(PlaybackStatus.Playing, snap.Status);
        Assert.Equal(Track2.Id, _playlist.CurrentTrack!.Id);
        Assert.Equal("Song Two", snap.CurrentTrack!.Title);
    }

    [Fact]
    public async Task TrackEnded_at_end_of_Sequential_queue_preserves_track_as_Stopped()
    {
        _playlist.AddTrack(Track1);
        _playlist.PlaybackMode = PlaybackMode.Sequential;
        await PlayTrackAsync(Track1);

        Assert.Equal(Track1.Id, _session.LatestSnapshot.CurrentTrack!.Id);

        // Fire TrackEnded -- queue has no next in Sequential mode
        _player.FireTrackEnded();

        var snap = await AwaitSnapshotAsync(s => s.Status == PlaybackStatus.Stopped);
        Assert.Equal(Track1.Id, snap.CurrentTrack!.Id);
        Assert.Equal(Track1.Id, _playlist.CurrentTrack!.Id);
    }

    [Fact]
    public async Task TrackEnded_with_RepeatAll_wraps_to_first_track()
    {
        _playlist.AddTrack(Track1);
        _playlist.AddTrack(Track2);
        _playlist.PlaybackMode = PlaybackMode.RepeatAll;
        await PlayTrackAsync(Track2); // Start at track 2

        _player.FireTrackEnded();

        var snap = await AwaitSnapshotAsync(s => s.CurrentTrack?.Id == Track1.Id);
        Assert.Equal(PlaybackStatus.Playing, snap.Status);
        Assert.Equal(Track1.Id, _playlist.CurrentTrack!.Id);
    }

    [Fact]
    public async Task TrackEnded_with_RepeatOne_repeats_current_track()
    {
        _playlist.AddTrack(Track1);
        _playlist.AddTrack(Track2);
        _playlist.PlaybackMode = PlaybackMode.RepeatOne;
        await PlayTrackAsync(Track1);

        _player.FireTrackEnded();

        var snap = await AwaitSnapshotAsync(s => s.Revision > 5); // wait for auto-advance publish
        Assert.Equal(Track1.Id, snap.CurrentTrack!.Id);
        Assert.Equal(PlaybackStatus.Playing, snap.Status);
        Assert.Equal(Track1.Id, _playlist.CurrentTrack!.Id);
    }

    [Fact]
    public async Task TrackEnded_on_empty_queue_does_not_crash()
    {
        // No tracks in queue, no current track
        _player.FireTrackEnded();

        // Should not crash; session should remain idle or stopped
        Assert.NotNull(_session.LatestSnapshot);
    }

    // ── Auto-advance failure ──

    [Fact]
    public async Task Failed_auto_advance_preserves_previous_track()
    {
        _playlist.AddTrack(Track1);
        _playlist.AddTrack(Track2);
        await PlayTrackAsync(Track1);

        // Configure the fake player to fail on the next PlayAsync
        _player.PlayAsyncError = (path, ct) =>
            path == Track2.FilePath
                ? new InvalidOperationException("Engine failure")
                : null;

        _player.FireTrackEnded();

        // Should remain on Track1 with Stopped status
        var snap = await AwaitSnapshotAsync(s => s.Status == PlaybackStatus.Stopped);
        Assert.Equal(Track1.Id, snap.CurrentTrack!.Id);
        Assert.Equal(Track1.Id, _playlist.CurrentTrack!.Id);
    }

    // ── Next command ──

    [Fact]
    public async Task NextAsync_advances_to_next_track()
    {
        _playlist.AddTrack(Track1);
        _playlist.AddTrack(Track2);
        await PlayTrackAsync(Track1);

        await _session.NextAsync();

        var snap = await AwaitSnapshotAsync(s => s.CurrentTrack?.Id == Track2.Id);
        Assert.Equal(PlaybackStatus.Playing, snap.Status);
        Assert.Equal(Track2.Id, _playlist.CurrentTrack!.Id);
    }

    [Fact]
    public async Task NextAsync_at_end_of_Sequential_queue_does_nothing()
    {
        _playlist.AddTrack(Track1);
        _playlist.PlaybackMode = PlaybackMode.Sequential;
        await PlayTrackAsync(Track1);

        await _session.NextAsync();

        // Should remain on Track1, still playing
        Assert.Equal(Track1.Id, _session.LatestSnapshot.CurrentTrack!.Id);
        Assert.Equal(PlaybackStatus.Playing, _session.LatestSnapshot.Status);
    }

    [Fact]
    public async Task NextAsync_with_RepeatAll_wraps_to_first_track()
    {
        _playlist.AddTrack(Track1);
        _playlist.AddTrack(Track2);
        _playlist.PlaybackMode = PlaybackMode.RepeatAll;
        await PlayTrackAsync(Track2);

        await _session.NextAsync();

        var snap = await AwaitSnapshotAsync(s => s.CurrentTrack?.Id == Track1.Id);
        Assert.Equal(PlaybackStatus.Playing, snap.Status);
    }

    // ── Previous command ──

    [Fact]
    public async Task PreviousAsync_goes_to_previous_track()
    {
        _playlist.AddTrack(Track1);
        _playlist.AddTrack(Track2);
        await PlayTrackAsync(Track2); // Start at track 2

        await _session.PreviousAsync();

        var snap = await AwaitSnapshotAsync(s => s.CurrentTrack?.Id == Track1.Id);
        Assert.Equal(PlaybackStatus.Playing, snap.Status);
        Assert.Equal(Track1.Id, _playlist.CurrentTrack!.Id);
    }

    [Fact]
    public async Task PreviousAsync_at_start_of_queue_does_nothing()
    {
        _playlist.AddTrack(Track1);
        _playlist.AddTrack(Track2);
        await PlayTrackAsync(Track1); // Already at start

        await _session.PreviousAsync();

        // Should remain on Track1, still playing
        Assert.Equal(Track1.Id, _session.LatestSnapshot.CurrentTrack!.Id);
        Assert.Equal(PlaybackStatus.Playing, _session.LatestSnapshot.Status);
    }

    // ── Next/Previous failure ──

    [Fact]
    public async Task NextAsync_failure_preserves_current_track()
    {
        _playlist.AddTrack(Track1);
        _playlist.AddTrack(Track2);
        await PlayTrackAsync(Track1);

        _player.PlayAsyncError = (path, ct) =>
            path == Track2.FilePath
                ? new InvalidOperationException("Engine failure")
                : null;

        await _session.NextAsync();

        // Should remain on Track1 with Stopped status
        Assert.Equal(Track1.Id, _session.LatestSnapshot.CurrentTrack!.Id);
        Assert.Equal(PlaybackStatus.Stopped, _session.LatestSnapshot.Status);
        Assert.Equal(Track1.Id, _playlist.CurrentTrack!.Id);
    }

    [Fact]
    public async Task PreviousAsync_failure_preserves_current_track()
    {
        _playlist.AddTrack(Track1);
        _playlist.AddTrack(Track2);
        await PlayTrackAsync(Track2);

        _player.PlayAsyncError = (path, ct) =>
            path == Track1.FilePath
                ? new InvalidOperationException("Engine failure")
                : null;

        await _session.PreviousAsync();

        Assert.Equal(Track2.Id, _session.LatestSnapshot.CurrentTrack!.Id);
        Assert.Equal(PlaybackStatus.Stopped, _session.LatestSnapshot.Status);
        Assert.Equal(Track2.Id, _playlist.CurrentTrack!.Id);
    }

    // ── Sequential queue with multiple tracks ──

    [Fact]
    public async Task Multiple_auto_advances_play_entire_queue()
    {
        _playlist.AddTrack(Track1);
        _playlist.AddTrack(Track2);
        _playlist.AddTrack(Track3);
        _playlist.PlaybackMode = PlaybackMode.Sequential;
        await PlayTrackAsync(Track1);

        // Track1 ends -> advances to Track2
        _player.FireTrackEnded();
        var snap2 = await AwaitSnapshotAsync(s => s.CurrentTrack?.Id == Track2.Id);
        Assert.Equal(Track2.Id, snap2.CurrentTrack!.Id);

        // Track2 ends -> advances to Track3
        _player.FireTrackEnded();
        var snap3 = await AwaitSnapshotAsync(s => s.CurrentTrack?.Id == Track3.Id);
        Assert.Equal(Track3.Id, snap3.CurrentTrack!.Id);

        // Track3 ends -> end of queue -> Stopped (preserving Track3)
        _player.FireTrackEnded();
        var snapEnd = await AwaitSnapshotAsync(s => s.Status == PlaybackStatus.Stopped);
        Assert.Equal(Track3.Id, snapEnd.CurrentTrack!.Id);
    }

    // ── Session disposal cancels auto-advance ──

    [Fact]
    public async Task Auto_advance_after_disposal_does_not_publish()
    {
        _playlist.AddTrack(Track1);
        _playlist.AddTrack(Track2);
        await PlayTrackAsync(Track1);

        var publishCountBeforeDisposal = 0;
        var publishCountAfterDisposal = 0;
        var disposed = false;

        using var sub = _session.Subscribe(_ =>
        {
            if (disposed)
                Interlocked.Increment(ref publishCountAfterDisposal);
            else
                Interlocked.Increment(ref publishCountBeforeDisposal);
        });

        var beforeSnapshot = publishCountBeforeDisposal;
        _session.Dispose();
        disposed = true;

        // Fire TrackEnded after disposal - should not publish
        _player.FireTrackEnded();

        // Small delay to allow any queued work to execute
        await Task.Delay(300);
        Assert.Equal(0, publishCountAfterDisposal);
    }
}
