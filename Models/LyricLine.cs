namespace AvaPlayer.Models;

public sealed class LyricLine
{
    public TimeSpan Time { get; init; }
    public string Text { get; init; } = string.Empty;
}
