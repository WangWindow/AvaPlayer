namespace AvaPlayer.Services.PlaybackSession;

/// <summary>
/// Typed result returned when attempting to start or change playback.
/// Callers pattern-match instead of catching opaque exceptions.
/// </summary>
public abstract record PlaybackStartResult
{
    private PlaybackStartResult() { }

    public sealed record Started : PlaybackStartResult;

    public sealed record Failed(PlaybackStartFailureKind Kind, string Message) : PlaybackStartResult;

    public bool IsSuccess => this is Started;
    public bool IsFailure => this is Failed;
}

public enum PlaybackStartFailureKind
{
    EngineUnavailable,
    FileNotFound,
    LoadFailed
}
