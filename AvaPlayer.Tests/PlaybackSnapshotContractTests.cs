using AvaPlayer.Models;
using AvaPlayer.Services.PlaybackSession;

namespace AvaPlayer.Application.Tests;

/// <summary>
/// Contract tests for <see cref="PlaybackSnapshot"/> immutability, defaults,
/// and value semantics.
/// </summary>
public sealed class PlaybackSnapshotContractTests
{
    [Fact]
    public void Idle_snapshot_has_correct_defaults()
    {
        var idle = PlaybackSnapshot.Idle;

        Assert.Equal(0, idle.Revision);
        Assert.Equal(PlaybackStatus.Stopped, idle.Status);
        Assert.Null(idle.CurrentTrack);
        Assert.False(idle.HasTrack);
        Assert.False(idle.IsPlaying);
        Assert.Equal(0, idle.Position);
        Assert.Equal(1, idle.Duration);
        Assert.Equal(80, idle.Volume);
        Assert.Equal(PlaybackMode.Sequential, idle.PlaybackMode);
    }

    [Fact]
    public void Snapshot_with_track_reports_HasTrack_true()
    {
        var track = new Track
        {
            Id = "t1",
            FilePath = "/music/song.mp3",
            Title = "Song",
            DurationSeconds = 200
        };

        var snap = PlaybackSnapshot.Idle with { CurrentTrack = track };

        Assert.True(snap.HasTrack);
        Assert.Same(track, snap.CurrentTrack);
    }

    [Fact]
    public void Snapshot_without_track_reports_HasTrack_false()
    {
        Assert.False(PlaybackSnapshot.Idle.HasTrack);
    }

    [Fact]
    public void IsPlaying_returns_true_only_when_Playing()
    {
        var playing = PlaybackSnapshot.Idle with { Status = PlaybackStatus.Playing };
        var paused = PlaybackSnapshot.Idle with { Status = PlaybackStatus.Paused };
        var stopped = PlaybackSnapshot.Idle with { Status = PlaybackStatus.Stopped };

        Assert.True(playing.IsPlaying);
        Assert.False(paused.IsPlaying);
        Assert.False(stopped.IsPlaying);
    }

    [Fact]
    public void Snapshot_is_immutable_record()
    {
        // Verify that `with` expressions create a new instance.
        var original = PlaybackSnapshot.Idle;
        var modified = original with { Volume = 50 };

        Assert.NotSame(original, modified);
        Assert.Equal(80, original.Volume);
        Assert.Equal(50, modified.Volume);
    }

    [Fact]
    public void Snapshot_revision_monotonically_increases()
    {
        var s1 = PlaybackSnapshot.Idle with { Revision = 1 };
        var s2 = PlaybackSnapshot.Idle with { Revision = 2 };
        var s3 = PlaybackSnapshot.Idle with { Revision = 3 };

        Assert.True(s1.Revision < s2.Revision);
        Assert.True(s2.Revision < s3.Revision);
    }

    [Fact]
    public void Two_snapshots_with_same_values_are_equal()
    {
        var a = new PlaybackSnapshot(0, PlaybackStatus.Stopped, null, 0, 1, 80, PlaybackMode.Sequential);
        var b = new PlaybackSnapshot(0, PlaybackStatus.Stopped, null, 0, 1, 80, PlaybackMode.Sequential);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}
