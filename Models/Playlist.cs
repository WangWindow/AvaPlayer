namespace AvaPlayer.Models;

public sealed class Playlist
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
}
