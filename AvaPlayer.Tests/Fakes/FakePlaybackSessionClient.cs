using AvaPlayer.Models;
using AvaPlayer.Services.PlaybackSession;

namespace AvaPlayer.Application.Tests.Fakes;

/// <summary>
/// Hand-written fake of <see cref="IPlaybackSessionClient"/> for testing
/// components that depend on the session contract without needing the real
/// <see cref="PlaybackSession"/> command-loop infrastructure.
/// </summary>
public sealed class FakePlaybackSessionClient : IPlaybackSessionClient
{
    private readonly List<Action<PlaybackSnapshot>> _subscribers = new();
    private readonly object _gate = new();

    public PlaybackSnapshot LatestSnapshot { get; private set; } = PlaybackSnapshot.Idle;

    /// <summary>
    /// When set to a non-null value, returned from <see cref="PlayTrackAsync"/> instead
    /// of the default <see cref="PlaybackStartResult.Started"/>.
    /// </summary>
    public PlaybackStartResult? PlayTrackResultOverride { get; set; }

    /// <summary>
    /// When set to a non-null value, returned from <see cref="GetSavedPositionAsync"/>.
    /// </summary>
    public double? SavedPositionOverride { get; set; }

    /// <summary>
    /// Tracks the number of times <see cref="PersistAsync"/> was called.
    /// </summary>
    public int PersistCallCount { get; private set; }

    /// <summary>
    /// Tracks the number of times <see cref="GetSavedPositionAsync"/> was called.
    /// </summary>
    public int GetSavedPositionCallCount { get; private set; }

    /// <summary>
    /// Records the last track passed to <see cref="PlayTrackAsync"/>.
    /// </summary>
    public Track? LastPlayedTrack { get; private set; }

    /// <summary>
    /// Records the last volume passed to <see cref="SetVolumeAsync"/>.
    /// </summary>
    public double? LastVolume { get; private set; }

    /// <summary>
    /// Records the last seek position passed to <see cref="SeekAsync"/>.
    /// </summary>
    public double? LastSeekPosition { get; private set; }

    /// <summary>
    /// Publishes a new snapshot to all subscribers. Updates <see cref="LatestSnapshot"/>.
    /// </summary>
    public void Publish(PlaybackSnapshot snapshot)
    {
        LatestSnapshot = snapshot;
        lock (_gate)
        {
            foreach (var sub in _subscribers)
                sub(snapshot);
        }
    }

    public IDisposable Subscribe(Action<PlaybackSnapshot> onSnapshot)
    {
        lock (_gate) _subscribers.Add(onSnapshot);
        onSnapshot(LatestSnapshot);
        return new Unsubscriber(this, onSnapshot);
    }

    public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default)
    {
        if (LatestSnapshot.IsPlaying)
            Publish(LatestSnapshot with { Status = PlaybackStatus.Paused });
        else if (LatestSnapshot.HasTrack)
            Publish(LatestSnapshot with { Status = PlaybackStatus.Playing });
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        Publish(LatestSnapshot with { Status = PlaybackStatus.Paused });
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        Publish(LatestSnapshot with { Status = PlaybackStatus.Playing });
        return Task.CompletedTask;
    }

    public Task NextAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task PreviousAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task SeekAsync(double seconds, CancellationToken cancellationToken = default)
    {
        LastSeekPosition = seconds;
        Publish(LatestSnapshot with { Position = seconds });
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        LastVolume = volume;
        Publish(LatestSnapshot with { Volume = volume });
        return Task.CompletedTask;
    }

    public Task CyclePlaybackModeAsync(CancellationToken cancellationToken = default)
    {
        var next = LatestSnapshot.PlaybackMode switch
        {
            PlaybackMode.Sequential => PlaybackMode.RepeatAll,
            PlaybackMode.RepeatAll => PlaybackMode.RepeatOne,
            PlaybackMode.RepeatOne => PlaybackMode.Shuffle,
            PlaybackMode.Shuffle => PlaybackMode.Sequential,
            _ => PlaybackMode.Sequential
        };
        Publish(LatestSnapshot with { PlaybackMode = next });
        return Task.CompletedTask;
    }

    public Task<PlaybackStartResult> PlayTrackAsync(Track track, CancellationToken cancellationToken = default)
    {
        LastPlayedTrack = track;

        if (PlayTrackResultOverride is not null)
            return Task.FromResult(PlayTrackResultOverride);

        Publish(LatestSnapshot with
        {
            CurrentTrack = track,
            Status = PlaybackStatus.Playing,
            Duration = Math.Max(track.DurationSeconds, 1),
            Position = 0
        });
        return Task.FromResult<PlaybackStartResult>(new PlaybackStartResult.Started());
    }

    public Task RestoreTrackAsync(CancellationToken cancellationToken = default)
    {
        RestoreTrackCallCount++;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Tracks the number of times <see cref="RestoreTrackAsync"/> was called.
    /// </summary>
    public int RestoreTrackCallCount { get; private set; }

    public Task PersistAsync(CancellationToken cancellationToken = default)
    {
        PersistCallCount++;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records the last position passed to <see cref="RestorePlaybackAtPositionAsync"/>.
    /// </summary>
    public double? LastRestorePosition { get; private set; }

    /// <summary>
    /// Tracks the number of times <see cref="RestorePlaybackAtPositionAsync"/> was called.
    /// </summary>
    public int RestorePlaybackAtPositionCallCount { get; private set; }

    public Task RestorePlaybackAtPositionAsync(double positionSeconds, CancellationToken cancellationToken = default)
    {
        RestorePlaybackAtPositionCallCount++;
        LastRestorePosition = positionSeconds;

        if (!LatestSnapshot.HasTrack)
        {
            // Simulate restoring: publish a paused snapshot with the given position
            // using the last played track if available, or a minimal idle snapshot.
            var track = LastPlayedTrack;
            Publish(LatestSnapshot with
            {
                CurrentTrack = track,
                Status = PlaybackStatus.Paused,
                Position = positionSeconds,
                Duration = track is not null ? Math.Max(track.DurationSeconds, 1) : LatestSnapshot.Duration
            });
        }
        else
        {
            Publish(LatestSnapshot with
            {
                Status = PlaybackStatus.Paused,
                Position = positionSeconds
            });
        }

        return Task.CompletedTask;
    }

    public Task<double> GetSavedPositionAsync(CancellationToken cancellationToken = default)
    {
        GetSavedPositionCallCount++;
        return Task.FromResult(SavedPositionOverride ?? 0);
    }

    private sealed class Unsubscriber(FakePlaybackSessionClient owner, Action<PlaybackSnapshot> callback) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) owner._subscribers.Remove(callback);
        }
    }
}
