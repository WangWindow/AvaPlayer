using AvaPlayer.Models;
using AvaPlayer.Services.MediaTransport;

namespace AvaPlayer.Application.Tests.Fakes;

/// <summary>
/// Hand-written fake of <see cref="IMediaTransportService"/> for testing
/// snapshot-to-transport state projection logic without platform-specific
/// DBus or SMTC infrastructure.
/// </summary>
public sealed class FakeMediaTransportService : IMediaTransportService
{
    public Track? LastTrack { get; private set; }
    public bool? LastPlaybackState { get; private set; }
    public TimeSpan? LastPosition { get; private set; }
    public TimeSpan? LastDuration { get; private set; }
    public PlaybackMode? LastPlaybackMode { get; private set; }

    public int InitializeCallCount { get; set; }
    public int UpdateTrackCallCount { get; set; }
    public int UpdatePlaybackStateCallCount { get; set; }
    public int UpdatePositionCallCount { get; set; }
    public int UpdatePlaybackModeCallCount { get; set; }

    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler<TimeSpan>? SeekRequested;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        InitializeCallCount++;
        return Task.CompletedTask;
    }

    public Task UpdateTrackAsync(Track? track, CancellationToken cancellationToken = default)
    {
        LastTrack = track;
        LastPosition = TimeSpan.Zero; // Simulate MPRIS/SMTC position reset on track update
        UpdateTrackCallCount++;
        return Task.CompletedTask;
    }

    public void UpdatePlaybackState(bool isPlaying)
    {
        LastPlaybackState = isPlaying;
        UpdatePlaybackStateCallCount++;
    }

    public void UpdatePosition(TimeSpan position, TimeSpan duration)
    {
        LastPosition = position;
        LastDuration = duration;
        UpdatePositionCallCount++;
    }

    public void UpdatePlaybackMode(PlaybackMode playbackMode)
    {
        LastPlaybackMode = playbackMode;
        UpdatePlaybackModeCallCount++;
    }

    public void Dispose()
    {
    }
}
