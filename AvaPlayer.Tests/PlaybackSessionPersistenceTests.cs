using AvaPlayer.Models;
using AvaPlayer.Services.PlaybackSession;

namespace AvaPlayer.Application.Tests;

/// <summary>
/// Tests for persistence semantics of <see cref="IPlaybackSessionClient"/>.
/// Verifies persist/restore behavior and position tracking.
/// </summary>
public sealed class PlaybackSessionPersistenceTests
{
    private static readonly Track SampleTrack = new()
    {
        Id = "t1",
        FilePath = "/music/test.mp3",
        Title = "Test Song",
        DurationSeconds = 240
    };

    [Fact]
    public async Task PersistAsync_increments_call_count()
    {
        var client = new Fakes.FakePlaybackSessionClient();

        await client.PersistAsync();
        await client.PersistAsync();
        await client.PersistAsync();

        Assert.Equal(3, client.PersistCallCount);
    }

    [Fact]
    public async Task GetSavedPositionAsync_returns_zero_by_default()
    {
        var client = new Fakes.FakePlaybackSessionClient();

        var pos = await client.GetSavedPositionAsync();

        Assert.Equal(0, pos);
    }

    [Fact]
    public async Task GetSavedPositionAsync_returns_override_when_set()
    {
        var client = new Fakes.FakePlaybackSessionClient();
        client.SavedPositionOverride = 42.5;

        var pos = await client.GetSavedPositionAsync();

        Assert.Equal(42.5, pos);
    }

    [Fact]
    public async Task GetSavedPositionAsync_increments_call_count()
    {
        var client = new Fakes.FakePlaybackSessionClient();

        await client.GetSavedPositionAsync();
        await client.GetSavedPositionAsync();

        Assert.Equal(2, client.GetSavedPositionCallCount);
    }

    [Fact]
    public async Task Persist_does_not_change_snapshot()
    {
        var client = new Fakes.FakePlaybackSessionClient();
        var track = new Track { Id = "t1", FilePath = "/s.mp3", Title = "S" };

        client.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = track,
            Status = PlaybackStatus.Playing,
            Position = 30,
            Revision = 3
        });

        var snapBefore = client.LatestSnapshot;
        await client.PersistAsync();

        Assert.Same(snapBefore, client.LatestSnapshot);
        Assert.Equal(30, client.LatestSnapshot.Position);
    }

    [Fact]
    public async Task Pause_then_persist_preserves_paused_position()
    {
        var client = new Fakes.FakePlaybackSessionClient();
        client.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Playing,
            Position = 45.2
        });

        await client.PauseAsync();
        await client.PersistAsync();

        Assert.Equal(PlaybackStatus.Paused, client.LatestSnapshot.Status);
        Assert.Equal(45.2, client.LatestSnapshot.Position);
    }

    [Fact]
    public async Task Seek_then_persist_preserves_seeked_position()
    {
        var client = new Fakes.FakePlaybackSessionClient();
        client.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Playing
        });

        await client.SeekAsync(120.5);
        await client.PersistAsync();

        Assert.Equal(120.5, client.LatestSnapshot.Position);
    }
}
