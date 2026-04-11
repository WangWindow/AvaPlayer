using AvaPlayer.Models;

namespace AvaPlayer.Services.Lyrics;

public interface ILyricsProvider
{
    string Name { get; }
    Task<IReadOnlyList<LyricLine>?> GetLyricsAsync(Track track, CancellationToken cancellationToken = default);
}
