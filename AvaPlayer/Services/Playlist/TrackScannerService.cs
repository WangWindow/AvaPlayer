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

    private static readonly EnumerationOptions ScanEnumerationOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        ReturnSpecialDirectories = false
    };

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

            try
            {
                foreach (var filePath in Directory.EnumerateFiles(folderPath, "*.*", ScanEnumerationOptions))
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
                        _logger.LogWarning(ex, "[Scanner] 跳过文件 {FilePath}: {Message}", filePath, ex.Message);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _logger.LogWarning(ex, "[Scanner] 扫描文件夹 {FolderPath} 时发生错误: {Message}", folderPath, ex.Message);
            }

            return tracks;
        }, cancellationToken);
    }

    private static string BuildTrackId(string filePath) => StableId.ForPath(filePath);
}
