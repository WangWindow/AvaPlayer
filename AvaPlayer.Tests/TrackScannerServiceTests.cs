using AvaPlayer.Services.Playlist;
using Microsoft.Extensions.Logging.Abstractions;

namespace AvaPlayer.Application.Tests;

public sealed class TrackScannerServiceTests : IDisposable
{
    private readonly string _tempDir;

    public TrackScannerServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"avaplayer-scanner-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ScanFolderAsync_WhenTagLibReadFails_StillReturnsTrackWithFallbackMetadata()
    {
        var filePath = Path.Combine(_tempDir, "broken.mp3");
        await File.WriteAllTextAsync(filePath, string.Empty);
        var service = new TrackScannerService(NullLogger<TrackScannerService>.Instance);

        var tracks = await service.ScanFolderAsync(_tempDir);

        var track = Assert.Single(tracks);
        Assert.Equal(filePath, track.FilePath);
        Assert.Equal("broken", track.Title);
        Assert.Equal(0, track.DurationSeconds);
    }
}
