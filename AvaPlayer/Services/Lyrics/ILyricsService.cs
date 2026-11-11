using AvaPlayer.Models;

namespace AvaPlayer.Services.Lyrics;

/// <summary>
/// Resolves timed or plain lyrics for a track from local sources, cache, and configured providers.
/// </summary>
public interface ILyricsService
{
    Task<IReadOnlyList<LyricLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default);
}
