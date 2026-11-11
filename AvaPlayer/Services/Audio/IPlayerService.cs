namespace AvaPlayer.Services.Audio;

/// <summary>
/// Low-level audio backend seam used by the playback session.
/// </summary>
/// <remarks>
/// Position and duration are measured in seconds; volume uses the UI's 0-100 scale.
/// Implementations raise state events after the corresponding playback state changes.
/// </remarks>
public interface IPlayerService : IDisposable
{
    bool IsReady { get; }
    string? InitializationError { get; }
    bool IsPlaying { get; }
    double Position { get; }
    double Duration { get; }
    double Volume { get; set; }

    event EventHandler<bool>? PlaybackStateChanged;
    event EventHandler<double>? PositionChanged;
    event EventHandler? TrackLoaded;
    event EventHandler? TrackEnded;

    Task PlayAsync(
        string filePath,
        bool startPaused = false,
        double startPositionSeconds = 0,
        CancellationToken cancellationToken = default);
    void Pause();
    void Resume();
    void Stop();
    void Seek(double seconds);
}
