namespace AvaPlayer.Models;

public sealed class LyricLine
{
    public bool IsTimed { get; init; } = true;
    public TimeSpan Time { get; init; }
    public string Text { get; init; } = string.Empty;
}
