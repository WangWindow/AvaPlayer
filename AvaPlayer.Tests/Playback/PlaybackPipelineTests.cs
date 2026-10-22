using AvaPlayer.Models;
using AvaPlayer.Services.PlaybackSession;
using AvaPlayer.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Tests;

public sealed class PlaybackPipelineTests : IDisposable
{
    private readonly FakePlayerService _player = new();
    private readonly FakePlaylistService _playlist = new();
    private readonly PlaybackSession _session;

    public PlaybackPipelineTests() => _session = new PlaybackSession(_player, _playlist,
        new FakeSettingsService(), new FakePlaybackPositionMemoryService(), NullLogger<PlaybackSession>.Instance);

    [Fact]
    public async Task Cancelled_queued_command_never_changes_playback()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _player.PlayAsyncOverride = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var start = _session.PlayTrackAsync(new Track { FilePath = "one.wav" });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancelled = new CancellationTokenSource();
        var pause = _session.PauseAsync(cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pause.WaitAsync(TimeSpan.FromSeconds(5)));
        release.SetResult();
        await start;
        await _session.SetVolumeAsync(60); // Drain the queue after the cancelled command.
        Assert.Equal(PlaybackStatus.Playing, _session.LatestSnapshot.Status);
    }

    [Fact]
    public async Task Disposing_session_completes_inflight_and_pending_commands()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _player.PlayAsyncOverride = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var start = _session.PlayTrackAsync(new Track { FilePath = "one.wav" });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = _session.NextAsync();
        _session.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _session.PauseAsync());
    }

    [Fact]
    public async Task Starting_track_publishes_coherent_track_and_engine_duration()
    {
        var first = new Track { Id = "one", FilePath = "one.wav", DurationSeconds = 100 };
        var second = new Track { Id = "two", FilePath = "two.wav", DurationSeconds = 200 };
        await _session.PlayTrackAsync(first);
        await _session.SeekAsync(30);
        var snapshots = new List<PlaybackSnapshot>();
        using var subscription = _session.Subscribe(snapshots.Add);
        snapshots.Clear();
        _player.Duration = 180;
        await _session.PlayTrackAsync(second);
        Assert.All(snapshots, snapshot => Assert.Same(second, snapshot.CurrentTrack));
        Assert.Equal(180, _session.LatestSnapshot.Duration);
        Assert.Equal(0, _session.LatestSnapshot.Position);
    }

    [Fact]
    public async Task Subscriber_can_unsubscribe_or_throw_without_blocking_other_observers()
    {
        IDisposable? self = null;
        self = _session.Subscribe(snapshot => { if (snapshot.Revision > 0) self?.Dispose(); });
        using var failing = _session.Subscribe(snapshot => { if (snapshot.Revision > 0) throw new InvalidOperationException("Test observer"); });
        PlaybackSnapshot? received = null;
        using var observer = _session.Subscribe(snapshot => received = snapshot);
        await _session.SetVolumeAsync(45);
        Assert.Equal(45, received!.Volume);
        await _session.SetVolumeAsync(50);
        Assert.Equal(50, received.Volume);
    }

    [Fact]
    public async Task Empty_resume_does_not_report_playing()
    {
        await _session.ResumeAsync();
        Assert.False(_session.LatestSnapshot.IsPlaying);
        Assert.False(_session.LatestSnapshot.HasTrack);
    }

    public void Dispose() => _session.Dispose();
}
