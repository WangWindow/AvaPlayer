using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AvaPlayer.Models;

namespace AvaPlayer.Services.AlbumArt;

public sealed partial class ItunesAlbumArtProvider(IHttpClientFactory httpClientFactory) : IAlbumArtProvider
{
    private const int MaxSearchResults = 8;
    private const int MinimumMatchScore = 60;
    private const int MaxJsonResponseBytes = 2 * 1024 * 1024;
    private const int MaxArtworkBytes = 8 * 1024 * 1024;

    [GeneratedRegex(@"(\(.*?\)|（.*?）|\[.*?\]|【.*?】)", RegexOptions.Compiled)]
    private static partial Regex DecorationRegex { get; }

    private static readonly string[] VariantMarkers =
    [
        "live",
        "remix",
        "acoustic",
        "instrumental",
        "karaoke",
        "demo",
        "remaster",
        "edit",
        "extended",
        "cover",
        "tv size",
        "dj",
        "现场",
        "伴奏",
        "纯音乐"
    ];

    public string Name => "iTunes";
    public bool RequiresNetwork => true;

    public async Task<byte[]?> GetAlbumArtAsync(Track track, CancellationToken cancellationToken = default)
    {
        var metadata = CreateTrackMetadata(track);
        var client = httpClientFactory.CreateClient();

        var terms = new[]
        {
            track.DisplayArtist,
            string.IsNullOrWhiteSpace(track.Album) ? null : track.DisplayAlbum,
            track.DisplayTitle
        };
        var term = Uri.EscapeDataString(
            string.Join(' ', terms.Where(static value => !string.IsNullOrWhiteSpace(value))));

        using var response = await client.GetAsync(
            $"https://itunes.apple.com/search?term={term}&entity=song&limit={MaxSearchResults}",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        using var document = await ReadJsonDocumentAsync(response, cancellationToken);
        if (!document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array ||
            results.GetArrayLength() == 0)
        {
            return null;
        }

        ItunesSong? bestSong = null;
        var bestScore = int.MinValue;

        foreach (var resultElement in results.EnumerateArray())
        {
            if (!TryParseSong(resultElement, out var song) ||
                string.IsNullOrWhiteSpace(song.ArtworkUrl))
            {
                continue;
            }

            var score = ScoreSong(metadata, song);
            if (score < MinimumMatchScore || score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestSong = song;
        }

        if (bestSong is not { } match)
        {
            return null;
        }

        var artworkUrl = ResizeArtworkUrl(match.ArtworkUrl);
        using var artworkResponse = await client.GetAsync(
            artworkUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (artworkResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        artworkResponse.EnsureSuccessStatusCode();
        return await ReadBoundedBytesAsync(artworkResponse, MaxArtworkBytes, cancellationToken);
    }

    private static TrackMetadata CreateTrackMetadata(Track track) =>
        new(
            track.DisplayTitle,
            NormalizeSearchText(track.DisplayTitle),
            NormalizeSearchText(track.Artist),
            NormalizeSearchText(track.Album),
            track.DurationSeconds);

    private static int ScoreSong(TrackMetadata track, ItunesSong song)
    {
        // Require at least a fuzzy title match before considering the result at all.
        var titleScore = ScoreTextMatch(
            track.NormalizedTitle,
            NormalizeSearchText(song.TrackName),
            exactScore: 58,
            strongScore: 42,
            fuzzyScore: 26);
        if (titleScore < 26)
        {
            return int.MinValue;
        }

        var score = titleScore;

        if (!string.IsNullOrWhiteSpace(track.NormalizedArtist))
        {
            var artistScore = ScoreTextMatch(
                track.NormalizedArtist,
                NormalizeSearchText(song.ArtistName),
                exactScore: 30,
                strongScore: 20,
                fuzzyScore: 12);

            // A different artist is almost always a different recording, even with the same title.
            if (artistScore < 12)
            {
                return int.MinValue;
            }

            score += artistScore;
        }

        if (!string.IsNullOrWhiteSpace(track.NormalizedAlbum))
        {
            var albumScore = ScoreTextMatch(
                track.NormalizedAlbum,
                NormalizeSearchText(song.CollectionName),
                exactScore: 14,
                strongScore: 8,
                fuzzyScore: 4);

            // A fuzzy title with an unrelated album is too weak to trust.
            if (albumScore == 0 && titleScore < 42)
            {
                return int.MinValue;
            }

            score += albumScore;
        }

        if (track.DurationSeconds > 1 && song.DurationSeconds > 0)
        {
            var delta = Math.Abs(track.DurationSeconds - song.DurationSeconds);
            score += delta switch
            {
                <= 2 => 20,
                <= 5 => 12,
                <= 10 => 6,
                >= 30 => -20,
                >= 15 => -10,
                _ => 0
            };
        }

        score -= CalculateVariantPenalty(track.DisplayTitle, song.TrackName);
        return score;
    }

    private static bool TryParseSong(JsonElement element, out ItunesSong song)
    {
        song = default;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var trackName = GetString(element, "trackName");
        if (string.IsNullOrWhiteSpace(trackName))
        {
            return false;
        }

        var durationSeconds = 0d;
        if (element.TryGetProperty("trackTimeMillis", out var durationElement) &&
            durationElement.ValueKind == JsonValueKind.Number)
        {
            durationSeconds = Math.Max(0, durationElement.GetInt64() / 1000d);
        }

        song = new ItunesSong(
            trackName,
            GetString(element, "artistName"),
            GetString(element, "collectionName"),
            GetString(element, "artworkUrl100"),
            durationSeconds);
        return true;
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static int ScoreTextMatch(string expected, string actual, int exactScore, int strongScore, int fuzzyScore)
    {
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return 0;
        }

        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return exactScore;
        }

        if (expected.Contains(actual, StringComparison.Ordinal) || actual.Contains(expected, StringComparison.Ordinal))
        {
            return strongScore;
        }

        var similarity = CalculateDiceSimilarity(expected, actual);
        return similarity switch
        {
            >= 0.88 => strongScore,
            >= 0.72 => fuzzyScore,
            >= 0.60 => Math.Max(2, fuzzyScore / 2),
            _ => 0
        };
    }

    private static string NormalizeSearchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var stripped = DecorationRegex.Replace(value, " ");
        var builder = new StringBuilder(stripped.Length);

        foreach (var character in stripped)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static int CalculateVariantPenalty(string expectedTitle, string resultTitle)
    {
        var expectedMarkers = ExtractVariantMarkers(expectedTitle);
        var resultMarkers = ExtractVariantMarkers(resultTitle);

        var penalty = 0;

        foreach (var marker in resultMarkers)
        {
            if (!expectedMarkers.Contains(marker))
            {
                penalty += 14;
            }
        }

        foreach (var marker in expectedMarkers)
        {
            if (!resultMarkers.Contains(marker))
            {
                penalty += 6;
            }
        }

        return penalty;
    }

    private static HashSet<string> ExtractVariantMarkers(string title)
    {
        var normalizedTitle = NormalizeVariantText(title);
        var markers = new HashSet<string>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(normalizedTitle))
        {
            return markers;
        }

        foreach (var marker in VariantMarkers)
        {
            var normalizedMarker = NormalizeVariantText(marker);
            if (normalizedTitle.Contains(normalizedMarker, StringComparison.Ordinal))
            {
                markers.Add(normalizedMarker);
            }
        }

        return markers;
    }

    private static string NormalizeVariantText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static double CalculateDiceSimilarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        if (left.Length == 1 || right.Length == 1)
        {
            return string.Equals(left, right, StringComparison.Ordinal) ? 1 : 0;
        }

        var rightBigrams = new Dictionary<uint, int>(right.Length);
        for (var i = 0; i < right.Length - 1; i++)
        {
            var key = ToBigramKey(right[i], right[i + 1]);
            rightBigrams.TryGetValue(key, out var count);
            rightBigrams[key] = count + 1;
        }

        var matches = 0;
        for (var i = 0; i < left.Length - 1; i++)
        {
            var key = ToBigramKey(left[i], left[i + 1]);
            if (!rightBigrams.TryGetValue(key, out var count) || count == 0)
            {
                continue;
            }

            matches++;
            rightBigrams[key] = count - 1;
        }

        return (2d * matches) / ((left.Length - 1) + (right.Length - 1));
    }

    private static uint ToBigramKey(char first, char second) =>
        ((uint)first << 16) | second;

    private static string ResizeArtworkUrl(string artworkUrl) =>
        artworkUrl
            .Replace("100x100bb", "360x360bb", StringComparison.OrdinalIgnoreCase)
            .Replace("100x100", "360x360", StringComparison.OrdinalIgnoreCase);

    private static async Task<JsonDocument> ReadJsonDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxJsonResponseBytes)
        {
            throw new HttpRequestException($"iTunes response exceeded the {MaxJsonResponseBytes} byte limit.");
        }

        var capacity = response.Content.Headers.ContentLength is > 0 and <= MaxJsonResponseBytes
            ? (int)response.Content.Headers.ContentLength.Value
            : 0;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(capacity);
        var chunk = new byte[16 * 1024];

        while (true)
        {
            var bytesRead = await stream.ReadAsync(chunk, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            if (buffer.Length + bytesRead > MaxJsonResponseBytes)
            {
                throw new HttpRequestException($"iTunes response exceeded the {MaxJsonResponseBytes} byte limit.");
            }

            buffer.Write(chunk, 0, bytesRead);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
    }

    private static async Task<byte[]?> ReadBoundedBytesAsync(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > maxBytes)
        {
            throw new HttpRequestException($"iTunes artwork exceeded the {maxBytes} byte limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];

        while (true)
        {
            var bytesRead = await stream.ReadAsync(chunk, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            if (buffer.Length + bytesRead > maxBytes)
            {
                throw new HttpRequestException($"iTunes artwork exceeded the {maxBytes} byte limit.");
            }

            buffer.Write(chunk, 0, bytesRead);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    private readonly record struct TrackMetadata(
        string DisplayTitle,
        string NormalizedTitle,
        string NormalizedArtist,
        string NormalizedAlbum,
        double DurationSeconds);

    private readonly record struct ItunesSong(
        string TrackName,
        string ArtistName,
        string CollectionName,
        string ArtworkUrl,
        double DurationSeconds);
}
