using AvaPlayer.Models;

namespace AvaPlayer.Services.Lyrics;

public interface ILyricsService
{
    Task<IReadOnlyList<LyricLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default);
}
