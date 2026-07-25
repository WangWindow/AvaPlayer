using AvaPlayer.Models;

namespace AvaPlayer.Services.Lyrics;

/// <summary>
/// Narrow interface for PlayerBarViewModel to interact with lyric presentation
/// without a direct dependency on the concrete LyricsViewModel.
/// </summary>
public interface ILyricPresentationService
{
    event EventHandler<TimeSpan>? SeekRequested;

    void BeginLoading();
    void LoadLyrics(IReadOnlyList<LyricLine> lines);
    void ClearLyrics();
    void UpdatePosition(double positionSeconds);
}
