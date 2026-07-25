namespace AvaPlayer.Services.Lyrics;

public interface ILyricsProviderManager
{
    IReadOnlyList<ILyricsProvider> Providers { get; }
}
