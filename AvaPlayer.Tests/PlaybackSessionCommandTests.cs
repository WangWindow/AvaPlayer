using AvaPlayer.Models;
using AvaPlayer.Services.PlaybackSession;

namespace AvaPlayer.Application.Tests;

/// <summary>
/// Tests for <see cref="IPlaybackSessionClient"/> command behavior using
/// <see cref="Fakes.FakePlaybackSessionClient"/>. Covers pause, seek, volume,
/// playback-mode cycling, and play-track with typed results.
/// </summary>
public sealed class PlaybackSessionCommandTests
{
    private static readonly Track SampleTrack = new()
    {
        Id = "t1",
        FilePath = "/music/test.mp3",
        Title = "Test Song",
        DurationSeconds = 240
    };

    [Fact]
    public async Task PauseAsync_sets_status_to_Paused()
    {
        var client = new Fakes.FakePlaybackSessionClient();
        client.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Playing
        });

        await client.PauseAsync();

        Assert.Equal(PlaybackStatus.Paused, client.LatestSnapshot.Status);
    }

    [Fact]
    public async Task ResumeAsync_sets_status_to_Playing()
    {
        var client = new Fakes.FakePlaybackSessionClient();
        client.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Paused
        });

        await client.ResumeAsync();

        Assert.Equal(PlaybackStatus.Playing, client.LatestSnapshot.Status);
    }

    [Fact]
    public async Task SeekAsync_updates_position()
    {
        var client = new Fakes.FakePlaybackSessionClient();

        await client.SeekAsync(42.5);

        Assert.Equal(42.5, client.LatestSnapshot.Position);
        Assert.Equal(42.5, client.LastSeekPosition);
    }

    [Fact]
    public async Task SetVolumeAsync_clamps_and_updates()
    {
        var client = new Fakes.FakePlaybackSessionClient();

        await client.SetVolumeAsync(65);

        Assert.Equal(65, client.LatestSnapshot.Volume);
        Assert.Equal(65, client.LastVolume);
    }

    [Fact]
    public async Task CyclePlaybackModeAsync_cycles_through_modes()
    {
        var client = new Fakes.FakePlaybackSessionClient();

        // Start Sequential -> RepeatAll
        await client.CyclePlaybackModeAsync();
        Assert.Equal(PlaybackMode.RepeatAll, client.LatestSnapshot.PlaybackMode);

        // RepeatAll -> RepeatOne
        await client.CyclePlaybackModeAsync();
        Assert.Equal(PlaybackMode.RepeatOne, client.LatestSnapshot.PlaybackMode);

        // RepeatOne -> Shuffle
        await client.CyclePlaybackModeAsync();
        Assert.Equal(PlaybackMode.Shuffle, client.LatestSnapshot.PlaybackMode);

        // Shuffle -> Sequential
        await client.CyclePlaybackModeAsync();
        Assert.Equal(PlaybackMode.Sequential, client.LatestSnapshot.PlaybackMode);
    }

    [Fact]
    public async Task PlayTrackAsync_returns_Started_on_success()
    {
        var client = new Fakes.FakePlaybackSessionClient();

        var result = await client.PlayTrackAsync(SampleTrack);

        Assert.IsType<PlaybackStartResult.Started>(result);
        Assert.True(result.IsSuccess);
        Assert.Same(SampleTrack, client.LatestSnapshot.CurrentTrack);
        Assert.Equal(PlaybackStatus.Playing, client.LatestSnapshot.Status);
        Assert.Same(SampleTrack, client.LastPlayedTrack);
    }

    [Fact]
    public async Task PlayTrackAsync_returns_Failed_when_override_set()
    {
        var client = new Fakes.FakePlaybackSessionClient();
        client.PlayTrackResultOverride = new PlaybackStartResult.Failed(
            PlaybackStartFailureKind.FileNotFound, "/missing.mp3");

        // Publish a snapshot with a track so we can verify it's preserved on failure
        client.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Playing,
            Revision = 1
        });

        var result = await client.PlayTrackAsync(SampleTrack);

        var failed = Assert.IsType<PlaybackStartResult.Failed>(result);
        Assert.Equal(PlaybackStartFailureKind.FileNotFound, failed.Kind);
        Assert.True(result.IsFailure);

        // Failed start must NOT change the current snapshot
        Assert.Same(SampleTrack, client.LatestSnapshot.CurrentTrack);
        Assert.Equal(PlaybackStatus.Playing, client.LatestSnapshot.Status);
    }

    [Fact]
    public async Task TogglePlayPauseAsync_from_playing_pauses()
    {
        var client = new Fakes.FakePlaybackSessionClient();
        client.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Playing
        });

        await client.TogglePlayPauseAsync();

        Assert.Equal(PlaybackStatus.Paused, client.LatestSnapshot.Status);
    }

    [Fact]
    public async Task TogglePlayPauseAsync_from_paused_resumes()
    {
        var client = new Fakes.FakePlaybackSessionClient();
        client.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = SampleTrack,
            Status = PlaybackStatus.Paused
        });

        await client.TogglePlayPauseAsync();

        Assert.Equal(PlaybackStatus.Playing, client.LatestSnapshot.Status);
    }
}
