using AvaPlayer.Models;

namespace AvaPlayer.Services.PlaybackSession;

/// <summary>
/// Client contract for the authoritative playback session.
/// UI ViewModels and service adapters interact with playback exclusively through this interface.
/// </summary>
public interface IPlaybackSessionClient
{
    /// <summary>
    /// Returns the most recent snapshot; never null.
    /// </summary>
    PlaybackSnapshot LatestSnapshot { get; }

    /// <summary>
    /// Subscribe to every new snapshot. The callback is invoked immediately
    /// with <see cref="LatestSnapshot"/> and on every subsequent change.
    /// Returns a disposable that unsubscribes.
    /// </summary>
    IDisposable Subscribe(Action<PlaybackSnapshot> onSnapshot);

    // ── Commands ──

    Task TogglePlayPauseAsync(CancellationToken cancellationToken = default);
    Task PauseAsync(CancellationToken cancellationToken = default);
    Task ResumeAsync(CancellationToken cancellationToken = default);
    Task NextAsync(CancellationToken cancellationToken = default);
    Task PreviousAsync(CancellationToken cancellationToken = default);
    Task SeekAsync(double seconds, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(double volume, CancellationToken cancellationToken = default);
    Task CyclePlaybackModeAsync(CancellationToken cancellationToken = default);
    Task<PlaybackStartResult> PlayTrackAsync(Track track, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores the current track from persisted session state (current-track-path).
    /// Should be called after the playlist queue is loaded. Safe no-op when no key exists.
    /// </summary>
    Task RestoreTrackAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores playback at the given position (paused) for the track already set
    /// by <see cref="RestoreTrackAsync"/>. If the session already has a track loaded,
    /// seeks to the given position and pauses. Safe no-op when no playlist track is set.
    /// </summary>
    Task RestorePlaybackAtPositionAsync(double positionSeconds, CancellationToken cancellationToken = default);

    Task PersistAsync(CancellationToken cancellationToken = default);
    Task<double> GetSavedPositionAsync(CancellationToken cancellationToken = default);
}
