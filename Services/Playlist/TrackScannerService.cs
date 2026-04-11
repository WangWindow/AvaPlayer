using System.Security.Cryptography;
using System.Text;
using AvaPlayer.Models;

namespace AvaPlayer.Services.Playlist;

public sealed class TrackScannerService : ITrackScannerService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aac",
        ".aiff",
        ".alac",
        ".ape",
        ".flac",
        ".m4a",
        ".mka",
        ".mp3",
        ".mp4",
        ".oga",
        ".ogg",
        ".opus",
        ".wav",
        ".wma"
    };

    public async Task<IReadOnlyList<Track>> ScanFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"音乐文件夹不存在: {folderPath}");
        }

        return await Task.Run(() =>
        {
            var tracks = new List<Track>();

            foreach (var filePath in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!SupportedExtensions.Contains(Path.GetExtension(filePath)))
                {
                    continue;
                }

                try
                {
                    using var tagFile = TagLib.File.Create(filePath);
                    tracks.Add(new Track
                    {
                        Id = BuildTrackId(filePath),
                        FilePath = filePath,
                        Title = tagFile.Tag.Title ?? string.Empty,
                        Artist = tagFile.Tag.FirstPerformer ?? string.Empty,
                        Album = tagFile.Tag.Album ?? string.Empty,
                        DurationSeconds = Math.Max(0, tagFile.Properties.Duration.TotalSeconds)
                    });
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Scanner] 跳过文件 {filePath}: {ex.Message}");
                }
            }

            return (IReadOnlyList<Track>)tracks
                .OrderBy(static track => track.Artist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static track => track.Album, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static track => track.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    private static string BuildTrackId(string filePath)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(filePath.ToLowerInvariant()));
        return Convert.ToHexString(bytes);
    }
}
