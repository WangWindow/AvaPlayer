namespace AvaPlayer.Services.Audio;

public interface IPlayerService : IDisposable
{
    bool IsReady { get; }
    string? InitializationError { get; }
    bool IsPlaying { get; }
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
