namespace AvaPlayer.Models;

public sealed class PlaylistInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string FolderPath { get; init; } = string.Empty;
    public bool IncludeSubfolders { get; init; } = true;
    public int TrackCount { get; init; }

    public string DisplayTrackCount => TrackCount > 0 ? $"{TrackCount} 首" : "空";
}
