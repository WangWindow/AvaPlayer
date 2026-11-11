using AvaPlayer.Models;

namespace AvaPlayer.Services.MediaTransport;

/// <summary>
/// Projects playback state to the platform media controls and forwards their commands.
/// </summary>
/// <remarks>
/// Platform adapters raise the request events; the playback session remains the source
/// of truth. Unsupported platforms use <see cref="NoopMediaTransportService"/>.
/// </remarks>
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
