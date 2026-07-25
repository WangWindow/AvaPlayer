using AvaPlayer.Models;
using AvaPlayer.Services.PlaybackSession;

namespace AvaPlayer.Application.Tests;

/// <summary>
/// Tests for subscription/replay semantics of <see cref="IPlaybackSessionClient"/>.
/// Uses hand-written fakes; never instantiates the real <see cref="PlaybackSession"/>.
/// </summary>
public sealed class PlaybackSessionSubscriptionTests
{
    [Fact]
    public void Subscribe_receives_latest_snapshot_immediately()
    {
        var client = new Fakes.FakePlaybackSessionClient();
        var track = new Track { Id = "t1", FilePath = "/s.mp3", Title = "S" };
        client.Publish(PlaybackSnapshot.Idle with
        {
            CurrentTrack = track,
            Status = PlaybackStatus.Playing,
            Revision = 5
        });

        PlaybackSnapshot? received = null;
        client.Subscribe(s => received = s);

        Assert.NotNull(received);
        Assert.Equal(5, received!.Revision);
        Assert.Same(track, received.CurrentTrack);
    }

    [Fact]
    public void Subscribe_receives_Idle_when_no_snapshot_published()
    {
        var client = new Fakes.FakePlaybackSessionClient();

        PlaybackSnapshot? received = null;
        client.Subscribe(s => received = s);

        Assert.NotNull(received);
        Assert.Same(PlaybackSnapshot.Idle, received);
    }

    [Fact]
    public void Subscriber_receives_subsequent_updates()
    {
        var client = new Fakes.FakePlaybackSessionClient();
        var received = new List<PlaybackSnapshot>();
        client.Subscribe(s => received.Add(s));

        client.Publish(PlaybackSnapshot.Idle with { Revision = 1, Position = 10 });
        client.Publish(PlaybackSnapshot.Idle with { Revision = 2, Position = 20 });

        Assert.Equal(3, received.Count); // initial + 2 updates
        Assert.Equal(0, received[0].Revision); // Idle
        Assert.Equal(1, received[1].Revision);
        Assert.Equal(2, received[2].Revision);
        Assert.Equal(20, received[2].Position);
    }

    [Fact]
    public void Dispose_unsubscribes_and_stops_notifications()
    {
        var client = new Fakes.FakePlaybackSessionClient();
        var received = new List<PlaybackSnapshot>();
        var sub = client.Subscribe(s => received.Add(s));

        sub.Dispose();

        client.Publish(PlaybackSnapshot.Idle with { Revision = 99 });

        Assert.Single(received); // only the initial Idle
    }

    [Fact]
    public void Multiple_subscribers_all_receive_updates()
    {
        var client = new Fakes.FakePlaybackSessionClient();
        var received1 = new List<PlaybackSnapshot>();
        var received2 = new List<PlaybackSnapshot>();

        client.Subscribe(s => received1.Add(s));
        client.Subscribe(s => received2.Add(s));

        client.Publish(PlaybackSnapshot.Idle with { Revision = 1 });

        Assert.Equal(2, received1.Count); // initial + 1 update
        Assert.Equal(2, received2.Count);
        Assert.Equal(1, received1[1].Revision);
        Assert.Equal(1, received2[1].Revision);
    }
}
