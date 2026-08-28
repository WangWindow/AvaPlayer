using AvaPlayer.Models;
using AvaPlayer.Services.Playlist;

namespace AvaPlayer.Application.Tests.Fakes;

public sealed class FakeTrackScannerService : ITrackScannerService
{
    public Func<string, IReadOnlyList<Track>>? ScanHandler { get; set; }

    public List<string> ScannedFolders { get; } = new();

    public Task<IReadOnlyList<Track>> ScanFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        ScannedFolders.Add(folderPath);
        var tracks = ScanHandler?.Invoke(folderPath) ?? [];
        return Task.FromResult(tracks);
    }
}
