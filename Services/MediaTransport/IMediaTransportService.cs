using AvaPlayer.Models;

namespace AvaPlayer.Services.MediaTransport;

public interface IMediaTransportService : IDisposable
{
    event EventHandler? PlayRequested;
    event EventHandler? PauseRequested;
    event EventHandler? NextRequested;
    event EventHandler? PreviousRequested;
    event EventHandler<TimeSpan>? SeekRequested;

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task UpdateTrackAsync(Track? track, CancellationToken cancellationToken = default);
    void UpdatePlaybackState(bool isPlaying);
    void UpdatePosition(TimeSpan position, TimeSpan duration);
    void UpdatePlaybackMode(PlaybackMode playbackMode);
}
