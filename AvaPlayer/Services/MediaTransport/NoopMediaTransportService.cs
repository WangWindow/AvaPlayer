#pragma warning disable CS0067
using AvaPlayer.Models;

namespace AvaPlayer.Services.MediaTransport;

public sealed class NoopMediaTransportService : IMediaTransportService
{
    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler<TimeSpan>? SeekRequested;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateTrackAsync(Track? track, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void UpdatePlaybackState(bool isPlaying)
    {
    }

    public void UpdatePosition(TimeSpan position, TimeSpan duration)
    {
    }

    public void UpdatePlaybackMode(PlaybackMode playbackMode)
    {
    }

    public void Dispose()
    {
    }
}
#pragma warning restore CS0067
