using System.Net;
using System.Text.Json;
using AvaPlayer.Helpers;
using AvaPlayer.Models;

namespace AvaPlayer.Services.Lyrics;

public sealed class LrcLibProvider(IHttpClientFactory httpClientFactory) : ILyricsProvider
{
    public string Name => "LRCLIB";

    public async Task<IReadOnlyList<LyricLine>?> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        var query = new List<string>
        {
            $"track_name={Uri.EscapeDataString(track.DisplayTitle)}",
            $"artist_name={Uri.EscapeDataString(track.DisplayArtist)}"
        };

        if (!string.IsNullOrWhiteSpace(track.Album))
        {
            query.Add($"album_name={Uri.EscapeDataString(track.DisplayAlbum)}");
        }

        if (track.DurationSeconds > 1)
        {
            query.Add($"duration={(int)Math.Round(track.DurationSeconds)}");
        }

        var client = httpClientFactory.CreateClient();
        using var response = await client.GetAsync($"https://lrclib.net/api/get?{string.Join("&", query)}", cancellationToken);
        // 404/204 是明确的「没有这首歌的歌词」;其余非成功状态(429/5xx 等)是瞬时故障,
        // 必须向上抛出,让 LyricsService 避免写入长期墓碑。
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (document.RootElement.TryGetProperty("syncedLyrics", out var syncedElement))
        {
            var syncedLyrics = syncedElement.GetString();
            if (!string.IsNullOrWhiteSpace(syncedLyrics))
            {
                return LrcParser.Parse(syncedLyrics);
            }
        }

        if (document.RootElement.TryGetProperty("plainLyrics", out var plainElement))
        {
            var plainLyrics = plainElement.GetString();
            if (!string.IsNullOrWhiteSpace(plainLyrics))
            {
                return LrcLibProviderHelpers.BuildEstimatedLyrics(plainLyrics, track.DurationSeconds);
            }
        }

        return null;
    }
}
