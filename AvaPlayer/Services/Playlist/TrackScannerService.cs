using AvaPlayer.Helpers;
using AvaPlayer.Models;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.Playlist;

public sealed class TrackScannerService : ITrackScannerService
{
    private readonly ILogger<TrackScannerService> _logger;

    public TrackScannerService(ILogger<TrackScannerService> logger)
    {
        _logger = logger;
    }

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

    public async Task<IReadOnlyList<Track>> ScanFolderAsync(string folderPath, bool includeSubfolders = true, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"音乐文件夹不存在: {folderPath}");
        }

        folderPath = FilePaths.Normalize(folderPath);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = includeSubfolders,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false
        };

        return await Task.Run(() =>
        {
            var tracks = new List<Track>();

            foreach (var filePath in Directory.EnumerateFiles(folderPath, "*.*", options))
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
                    _logger.LogWarning(ex, "[Scanner] 无法读取标签 {FilePath}: {Message}", filePath, ex.Message);
                    tracks.Add(new Track { Id = BuildTrackId(filePath), FilePath = filePath });
                }
            }

            return tracks;
        }, cancellationToken);
    }

    private static string BuildTrackId(string filePath) => StableId.ForPath(filePath);
}
