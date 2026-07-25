using AvaPlayer.Models;
using AvaPlayer.Services.PlaybackSession;
using AvaPlayer.Application.Tests.Fakes;

namespace AvaPlayer.Application.Tests;

/// <summary>
/// Tests for snapshot-to-media-transport state projection.
/// Verifies that subscribing to <see cref="IPlaybackSessionClient"/> snapshots
/// correctly drives <see cref="IMediaTransportService"/> state updates
/// (track metadata, playback state, position, duration).
/// </summary>
public sealed class MediaTransportStateProjectionTests
{
    private static readonly Track SampleTrack = new()
    {
        Id = "t1",
        FilePath = "/music/test.mp3",
        Title = "Test Song",
        Artist = "Artist",
        Album = "Album",
        DurationSeconds = 240
    };

    [Fact]
    public void Initial_snapshot_projects_track_and_state()
    {
        var session = new FakePlaybackSessionClient();
        var transport = new FakeMediaTransportService();

        session.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Playing,
            Position = 30,
            Duration = 240
        });

        transport.UpdatePlaybackState(session.LatestSnapshot.IsPlaying);
        transport.UpdatePosition(
            TimeSpan.FromSeconds(session.LatestSnapshot.Position),
            TimeSpan.FromSeconds(session.LatestSnapshot.Duration));

        Assert.Equal(30, transport.LastPosition!.Value.TotalSeconds);
        Assert.Equal(240, transport.LastDuration!.Value.TotalSeconds);
        Assert.True(transport.LastPlaybackState);
    }

    [Fact]
    public void Paused_snapshot_projects_stopped_state()
    {
        var session = new FakePlaybackSessionClient();
        var transport = new FakeMediaTransportService();

        session.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Paused,
            Position = 50
        });

        transport.UpdatePlaybackState(session.LatestSnapshot.IsPlaying);

        Assert.False(transport.LastPlaybackState);
    }

    [Fact]
    public void Subscribe_callback_updates_transport_on_every_snapshot()
    {
        var session = new FakePlaybackSessionClient();
        var transport = new FakeMediaTransportService();

        using var sub1 = session.Subscribe(snapshot =>
        {
            _ = transport.UpdateTrackAsync(snapshot.CurrentTrack);
            transport.UpdatePlaybackState(snapshot.IsPlaying);
            transport.UpdatePosition(
                TimeSpan.FromSeconds(Math.Max(0, snapshot.Position)),
                TimeSpan.FromSeconds(Math.Max(0, snapshot.Duration)));
        });

        // Reset counters after the initial idle callback from Subscribe
        transport.UpdateTrackCallCount = 0;
        transport.UpdatePlaybackStateCallCount = 0;
        transport.UpdatePositionCallCount = 0;

        // Snapshot 1: playing at position 10
        session.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Playing,
            Position = 10,
            Duration = 240
        });

        Assert.Same(SampleTrack, transport.LastTrack);
        Assert.True(transport.LastPlaybackState);
        Assert.Equal(10, transport.LastPosition!.Value.TotalSeconds);
        Assert.Equal(240, transport.LastDuration!.Value.TotalSeconds);
        Assert.Equal(1, transport.UpdateTrackCallCount);
        Assert.Equal(1, transport.UpdatePlaybackStateCallCount);
        Assert.Equal(1, transport.UpdatePositionCallCount);

        // Snapshot 2: paused at position 45
        session.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Paused,
            Position = 45,
            Duration = 240
        });

        Assert.False(transport.LastPlaybackState);
        Assert.Equal(45, transport.LastPosition!.Value.TotalSeconds);
        Assert.Equal(2, transport.UpdatePlaybackStateCallCount);
        Assert.Equal(2, transport.UpdatePositionCallCount);
    }

    [Fact]
    public void Subscribe_callback_with_no_track_projects_idle_state()
    {
        var session = new FakePlaybackSessionClient();
        var transport = new FakeMediaTransportService();

        using var sub2 = session.Subscribe(snapshot =>
        {
            _ = transport.UpdateTrackAsync(snapshot.CurrentTrack);
            transport.UpdatePlaybackState(snapshot.IsPlaying);
            transport.UpdatePosition(
                TimeSpan.FromSeconds(Math.Max(0, snapshot.Position)),
                TimeSpan.FromSeconds(Math.Max(0, snapshot.Duration)));
        });

        // Initial idle snapshot delivered on subscribe
        Assert.Null(transport.LastTrack);
        Assert.False(transport.LastPlaybackState);
        Assert.Equal(0, transport.LastPosition!.Value.TotalSeconds);
        Assert.Equal(1, transport.LastDuration!.Value.TotalSeconds);
    }

    [Fact]
    public void Subscribe_callback_clamps_negative_position_to_zero()
    {
        var session = new FakePlaybackSessionClient();
        var transport = new FakeMediaTransportService();

        using var sub3 = session.Subscribe(snapshot =>
        {
            transport.UpdatePosition(
                TimeSpan.FromSeconds(Math.Max(0, snapshot.Position)),
                TimeSpan.FromSeconds(Math.Max(0, snapshot.Duration)));
        });

        session.Publish(PlaybackSnapshot.Idle with
        {
            Position = -5,
            Duration = -1
        });

        Assert.Equal(0, transport.LastPosition!.Value.TotalSeconds);
        Assert.Equal(0, transport.LastDuration!.Value.TotalSeconds);
    }

    [Fact]
    public void Disposing_subscription_stops_transport_updates()
    {
        var session = new FakePlaybackSessionClient();
        var transport = new FakeMediaTransportService();

        var sub = session.Subscribe(snapshot =>
        {
            transport.UpdatePlaybackState(snapshot.IsPlaying);
        });

        // Clear initial call from subscribe
        transport.UpdatePlaybackStateCallCount = 0;

        sub.Dispose();

        session.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Playing
        });

        Assert.Equal(0, transport.UpdatePlaybackStateCallCount);
    }

    [Fact]
    public async Task Session_commands_route_directly()
    {
        var session = new FakePlaybackSessionClient();
        session.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Playing,
            Position = 30
        });

        await session.PauseAsync();
        Assert.Equal(PlaybackStatus.Paused, session.LatestSnapshot.Status);

        await session.ResumeAsync();
        Assert.Equal(PlaybackStatus.Playing, session.LatestSnapshot.Status);

        await session.SeekAsync(90);
        Assert.Equal(90, session.LatestSnapshot.Position);
    }

    [Fact]
    public void Subscribe_callback_projects_playback_mode()
    {
        var session = new FakePlaybackSessionClient();
        var transport = new FakeMediaTransportService();

        using var sub = session.Subscribe(snapshot =>
        {
            transport.UpdatePlaybackMode(snapshot.PlaybackMode);
        });

        // Mode starts as Sequential (from initial Idle callback on Subscribe)
        Assert.Equal(PlaybackMode.Sequential, transport.LastPlaybackMode);

        // Reset counter after the initial idle callback
        transport.UpdatePlaybackModeCallCount = 0;

        // Publish snapshot with Shuffle mode
        session.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Playing,
            PlaybackMode = PlaybackMode.Shuffle
        });

        Assert.Equal(PlaybackMode.Shuffle, transport.LastPlaybackMode);
        Assert.Equal(1, transport.UpdatePlaybackModeCallCount);

        // Mode cycles through values
        session.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Playing,
            PlaybackMode = PlaybackMode.RepeatOne
        });

        Assert.Equal(PlaybackMode.RepeatOne, transport.LastPlaybackMode);
        Assert.Equal(2, transport.UpdatePlaybackModeCallCount);
    }

    [Fact]
    public void Transport_track_update_reflects_session_current_track()
    {
        var session = new FakePlaybackSessionClient();
        var transport = new FakeMediaTransportService();

        using var sub4 = session.Subscribe(snapshot =>
        {
            _ = transport.UpdateTrackAsync(snapshot.CurrentTrack);
        });

        // Reset counter after the initial idle callback from Subscribe
        transport.UpdateTrackCallCount = 0;

        session.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Playing
        });

        Assert.Same(SampleTrack, transport.LastTrack);
        Assert.Equal(1, transport.UpdateTrackCallCount);

        var nextTrack = new Track
        {
            Id = "t2",
            FilePath = "/music/next.mp3",
            Title = "Next",
            DurationSeconds = 180
        };

        session.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = nextTrack,
            Status = PlaybackStatus.Playing
        });

        Assert.Same(nextTrack, transport.LastTrack);
        Assert.Equal(2, transport.UpdateTrackCallCount);
    }

    [Fact]
    public void UpdateTrackAsync_with_same_track_preserves_position()
    {
        // Simulates the snapshot projection ordering where OnTrackChanged
        // calls UpdateTrackAsync before OnMediaTransportSnapshot calls
        // UpdatePosition. This verifies the fix for MPRIS position 0 bug:
        // same-track UpdateTrackAsync must not reset _position.
        var transport = new FakeMediaTransportService();

        // First call: initial track set (simulates OnTrackChanged or first subscriber)
        _ = transport.UpdateTrackAsync(SampleTrack);
        Assert.Equal(TimeSpan.Zero, transport.LastPosition);

        // Second call: same track via second subscriber (should not reset position)
        _ = transport.UpdateTrackAsync(SampleTrack);
        Assert.Equal(TimeSpan.Zero, transport.LastPosition);

        // Position restore (simulates UpdatePosition after both track updates)
        transport.UpdatePosition(TimeSpan.FromSeconds(42.5), TimeSpan.FromSeconds(240));
        Assert.Equal(42.5, transport.LastPosition!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void UpdateTrackAsync_with_new_track_resets_position()
    {
        var transport = new FakeMediaTransportService();
        var track2 = new Track { Id = "t2", FilePath = "/music/other.mp3" };

        // Set initial track and position
        _ = transport.UpdateTrackAsync(SampleTrack);
        transport.UpdatePosition(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(240));
        Assert.Equal(30, transport.LastPosition!.Value.TotalSeconds);

        // New track should reset position to zero
        _ = transport.UpdateTrackAsync(track2);
        Assert.Equal(TimeSpan.Zero, transport.LastPosition);
    }

    [Fact]
    public void Subscribe_callback_ordering_preserves_restored_position()
    {
        // End-to-end verification: a snapshot with track + position is published.
        // The callback calls UpdateTrackAsync then UpdatePosition.
        // Position must end up at the snapshot value, not zero.
        var session = new FakePlaybackSessionClient();
        var transport = new FakeMediaTransportService();

        using var sub = session.Subscribe(snapshot =>
        {
            _ = transport.UpdateTrackAsync(snapshot.CurrentTrack);
            transport.UpdatePlaybackState(snapshot.IsPlaying);
            transport.UpdatePosition(
                TimeSpan.FromSeconds(Math.Max(0, snapshot.Position)),
                TimeSpan.FromSeconds(Math.Max(0, snapshot.Duration)));
        });

        // Publish a snapshot simulating session restore with position=42.5
        session.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Paused,
            Position = 42.5,
            Duration = 240
        });

        Assert.Same(SampleTrack, transport.LastTrack);
        Assert.Equal(42.5, transport.LastPosition!.Value.TotalSeconds, 3);
        Assert.Equal(240, transport.LastDuration!.Value.TotalSeconds);
        Assert.False(transport.LastPlaybackState);
    }

    [Fact]
    public void Second_subscriber_UpdateTrackAsync_does_not_zero_position()
    {
        // Simulates the real app flow where two subscribers process the same snapshot:
        // subscriber 1 (PlayerBarViewModel) calls UpdateTrackAsync (via TrackChanged),
        // subscriber 2 (App) calls UpdateTrackAsync then UpdatePosition.
        // The second UpdateTrackAsync must not zero the restored position.

        var transport = new FakeMediaTransportService();

        // Subscriber 1: OnTrackChanged from PlayerBarViewModel.ApplySnapshot
        _ = transport.UpdateTrackAsync(SampleTrack);

        // Subscriber 2: OnMediaTransportSnapshot
        _ = transport.UpdateTrackAsync(SampleTrack);
        transport.UpdatePlaybackState(false);
        transport.UpdatePosition(TimeSpan.FromSeconds(42.5), TimeSpan.FromSeconds(240));
        transport.UpdatePlaybackMode(PlaybackMode.Sequential);

        Assert.Same(SampleTrack, transport.LastTrack);
        Assert.Equal(42.5, transport.LastPosition!.Value.TotalSeconds, 3);
        Assert.Equal(240, transport.LastDuration!.Value.TotalSeconds);
        Assert.False(transport.LastPlaybackState);
    }
}
