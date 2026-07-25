using AvaPlayer.Models;

namespace AvaPlayer.Services.PlaybackSession;

/// <summary>
/// Complete authoritative playback snapshot. Immutable.
/// Consumers apply this to presentation state; they never reconcile multiple sources.
/// </summary>
/// <param name="Revision">Monotonic revision number; older snapshots are discarded.</param>
/// <param name="Status">Playback status.</param>
/// <param name="CurrentTrack">Currently loaded track, or null when idle.</param>
/// <param name="Position">Current position in seconds.</param>
/// <param name="Duration">Track duration in seconds, or 1 when no track is loaded.</param>
/// <param name="Volume">Volume 0-100.</param>
/// <param name="PlaybackMode">Active playback mode.</param>
public sealed record PlaybackSnapshot(
    long Revision,
    PlaybackStatus Status,
    Track? CurrentTrack,
    double Position,
    double Duration,
    double Volume,
    PlaybackMode PlaybackMode)
{
    public bool HasTrack => CurrentTrack is not null;
    public bool IsPlaying => Status == PlaybackStatus.Playing;

    /// <summary>
    /// The idle snapshot presented before any track has been loaded.
    /// </summary>
    public static PlaybackSnapshot Idle { get; } = new(
        Revision: 0,
        Status: PlaybackStatus.Stopped,
        CurrentTrack: null,
        Position: 0,
        Duration: 1,
        Volume: 80,
        PlaybackMode: PlaybackMode.Sequential);
}
