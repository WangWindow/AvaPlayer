using AvaPlayer.Services.Audio;

namespace AvaPlayer.Application.Tests.Fakes;

/// <summary>
/// Hand-written fake of <see cref="IPlayerService"/> for unit testing.
/// Tracks state explicitly; never touches native audio.
/// Models the MiniAudioPlayerService contract for position behavior:
/// - Pause saves the current position; Resume continues from it.
/// - Seek while paused also updates the saved paused position.
/// - PlayAsync fires both PlaybackStateChanged and TrackLoaded on success,
///   matching the real player's event sequence.
/// </summary>
public sealed class FakePlayerService : IPlayerService
{
    public bool IsReady { get; set; } = true;
    public string? InitializationError { get; set; }
    public bool IsPlaying { get; set; }

    /// <summary>
    /// Current playback position in seconds.
    /// When paused, returns the position saved at pause time.
    /// </summary>
    public double Position { get; set; }
    public double Duration { get; set; } = 1;
    public double Volume { get; set; } = 80;

    public string? LastPlayedFilePath { get; private set; }
    public bool LastStartPaused { get; private set; }
    public double LastStartPosition { get; private set; }

    /// <summary>
    /// Position saved when Pause() was called, or when startPaused
    /// was used with startPositionSeconds. Mimics MiniAudioPlayerService._pausedCursor.
    /// </summary>
    public double PausedPosition { get; private set; }

    public event EventHandler<bool>? PlaybackStateChanged;
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? TrackLoaded;
    public event EventHandler? TrackEnded;

    public FakePlayerService()
    {
    }

    public Task PlayAsync(
        string filePath,
        bool startPaused = false,
        double startPositionSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        if (PlayAsyncError is not null)
        {
            var error = PlayAsyncError(filePath, cancellationToken);
            if (error is not null)
                return Task.FromException(error);
        }

        LastPlayedFilePath = filePath;
        LastStartPaused = startPaused;
        LastStartPosition = startPositionSeconds;

        IsReady = true;
        IsPlaying = !startPaused;
        Position = startPositionSeconds;

        // Match MiniAudioPlayerService event sequence: always fire both
        // PlaybackStateChanged and TrackLoaded on success.
        PlaybackStateChanged?.Invoke(this, IsPlaying);
        TrackLoaded?.Invoke(this, EventArgs.Empty);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Simulates the natural end of the current track, firing
    /// PlaybackStateChanged (isPlaying = false) then TrackEnded,
    /// matching the MiniAudioPlayerService event sequence.
    /// </summary>
    public void FireTrackEnded()
    {
        IsPlaying = false;
        PlaybackStateChanged?.Invoke(this, false);
        TrackEnded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Fires <see cref="PositionChanged"/> with the current <see cref="Position"/>,
    /// simulating a timer tick from MiniAudioPlayerService.
    /// Test code cannot invoke the event directly (C# accessibility rules).
    /// </summary>
    public void FirePositionChanged()
    {
        PositionChanged?.Invoke(this, Position);
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        PausedPosition = Position;
        IsPlaying = false;
        PlaybackStateChanged?.Invoke(this, false);
    }

    public void Resume()
    {
        if (IsPlaying) return;
        IsPlaying = true;
        // Position stays at PausedPosition (was saved by Pause()).
        // Does NOT reset to 0 — matches MiniAudioPlayerService behavior.
        PlaybackStateChanged?.Invoke(this, true);
    }

    public void Stop()
    {
        IsPlaying = false;
        PausedPosition = 0;
        Position = 0;
        PlaybackStateChanged?.Invoke(this, false);
    }

    public void Seek(double seconds)
    {
        var clamped = Math.Max(0, seconds);
        Position = clamped;
        if (!IsPlaying)
            PausedPosition = clamped;
        PositionChanged?.Invoke(this, Position);
    }

    /// <summary>
    /// Simulates a failure on the next PlayAsync call.
    /// </summary>
    public Func<string, CancellationToken, Exception?>? PlayAsyncError { get; set; }

    public void Dispose()
    {
    }
}
