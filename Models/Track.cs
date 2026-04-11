using System.IO;

namespace AvaPlayer.Models;

public sealed class Track
{
    public string Id { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Album { get; init; } = string.Empty;
    public double DurationSeconds { get; init; }

    public string DisplayTitle =>
        string.IsNullOrWhiteSpace(Title)
            ? Path.GetFileNameWithoutExtension(FilePath)
            : Title;

    public string DisplayArtist =>
        string.IsNullOrWhiteSpace(Artist)
            ? "未知艺术家"
            : Artist;

    public string DisplayAlbum =>
        string.IsNullOrWhiteSpace(Album)
            ? "未知专辑"
            : Album;

    public string DisplayArtistAlbum =>
        string.IsNullOrWhiteSpace(Album)
            ? DisplayArtist
            : $"{DisplayArtist} · {DisplayAlbum}";

    public string DisplayDuration
    {
        get
        {
            var duration = TimeSpan.FromSeconds(Math.Max(0, DurationSeconds));
            return duration.Hours > 0
                ? $"{duration.Hours}:{duration.Minutes:D2}:{duration.Seconds:D2}"
                : $"{duration.Minutes}:{duration.Seconds:D2}";
        }
    }
}
