using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AvaPlayer.Helpers;
using AvaPlayer.Models;

namespace AvaPlayer.Services.Lyrics;

public sealed partial class NetEaseProvider(IHttpClientFactory httpClientFactory) : ILyricsProvider
{
    private const int MaxSearchResults = 10;
    private const int MaxLyricCandidates = 3;
    private const int MinimumCandidateScore = 48;
    private const int CandidateScoreWindow = 24;
    private const int StrongSearchScore = 90;
    private const double StrongCoverageRatio = 0.82;
    private const int MaxJsonResponseBytes = 2 * 1024 * 1024;

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

    public string Name => "NetEase";

    [GeneratedRegex(@"(\(.*?\)|（.*?）|\[.*?\]|【.*?】)", RegexOptions.Compiled)]
    private static partial Regex DecorationRegex();

    public async Task<IReadOnlyList<LyricLine>?> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        var metadata = CreateTrackMetadata(track);
        var candidates = await SearchSongCandidatesAsync(metadata, cancellationToken);
        if (candidates.Count == 0)
        {
            return null;
        }

        var client = httpClientFactory.CreateClient();
        return await FetchBestLyricsAsync(client, candidates, track, cancellationToken);
    }

    private async Task<IReadOnlyList<ScoredSongCandidate>> SearchSongCandidatesAsync(
        TrackMetadata metadata,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        var candidates = await SearchCandidatesAsync(
            client,
            BuildQueryText(metadata.DisplayTitle, metadata.Artist, metadata.Album),
            metadata,
            cancellationToken);

        // Only spend the extra round-trip when the fuller query produced no usable candidate.
        if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(metadata.Album))
        {
            candidates = await SearchCandidatesAsync(
                client,
                BuildQueryText(metadata.DisplayTitle, metadata.Artist),
                metadata,
                cancellationToken);
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        candidates.Sort(static (left, right) => right.SearchScore.CompareTo(left.SearchScore));

        var cutoff = Math.Max(MinimumCandidateScore, candidates[0].SearchScore - CandidateScoreWindow);
        return [.. candidates
            .Where(candidate => candidate.SearchScore >= cutoff)
            .Take(MaxLyricCandidates)];
    }

    private static async Task<List<ScoredSongCandidate>> SearchCandidatesAsync(
        HttpClient client,
        string queryText,
        TrackMetadata metadata,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return [];
        }

        using var request = CreateRequest(
            $"https://music.163.com/api/cloudsearch/pc?s={Uri.EscapeDataString(queryText)}&type=1&limit={MaxSearchResults}");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();

        using var document = await ReadJsonDocumentAsync(response, cancellationToken);
        if (!document.RootElement.TryGetProperty("result", out var resultElement) ||
            !resultElement.TryGetProperty("songs", out var songsElement) ||
            songsElement.ValueKind != JsonValueKind.Array ||
            songsElement.GetArrayLength() == 0)
        {
            return [];
        }

        var candidates = new List<ScoredSongCandidate>();
        foreach (var songElement in songsElement.EnumerateArray())
        {
            if (!TryParseCandidate(songElement, out var candidate))
            {
                continue;
            }

            var score = ScoreCandidate(metadata, candidate);
            if (score >= MinimumCandidateScore)
            {
                candidates.Add(new ScoredSongCandidate(candidate.Id, score));
            }
        }

        return candidates;
    }

    private static async Task<IReadOnlyList<LyricLine>?> FetchBestLyricsAsync(
        HttpClient client,
        IReadOnlyList<ScoredSongCandidate> candidates,
        Track track,
        CancellationToken cancellationToken)
    {
        // Bounded fan-out: at most MaxLyricCandidates requests are started, and the whole
        // group is cancelled as soon as one candidate is both high-scoring and high-quality.
        using var fetchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<CandidateMatch>>(candidates.Count);
        foreach (var candidate in candidates)
        {
            pending.Add(FetchCandidateAsync(client, candidate, track, fetchCts.Token));
        }

        LyricsMatch? bestMatch = null;
        Exception? transientFailure = null;

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending);
            pending.Remove(completed);

            var outcome = await completed;
            if (outcome.Failure is not null)
            {
                transientFailure ??= outcome.Failure;
                continue;
            }

            if (outcome.Match is not { } match)
            {
                continue;
            }

            if (!bestMatch.HasValue || match.Score > bestMatch.Value.Score)
            {
                bestMatch = match;
            }

            if (outcome.IsStrongMatch)
            {
                await fetchCts.CancelAsync();
                break;
            }
        }

        if (pending.Count > 0)
        {
            // FetchCandidateAsync converts cancellation into a normal empty outcome.
            await Task.WhenAll(pending);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (bestMatch is { } accepted)
        {
            return accepted.Lyrics;
        }

        // A transport or server failure is not evidence that lyrics do not exist; surface it
        // so the service layer can avoid negative-caching during an outage.
        if (transientFailure is not null)
        {
            ExceptionDispatchInfo.Capture(transientFailure).Throw();
        }

        return null;
    }

    private static async Task<CandidateMatch> FetchCandidateAsync(
        HttpClient client,
        ScoredSongCandidate candidate,
        Track track,
        CancellationToken cancellationToken)
    {
        try
        {
            var lyrics = await FetchLyricsAsync(client, candidate.Id, cancellationToken);
            if (lyrics is not { Count: > 0 })
            {
                return default;
            }

            var quality = LyricsQualityEvaluator.Evaluate(track, lyrics);
            var totalScore = candidate.SearchScore + quality.Score;
            if (!quality.IsAccepted)
            {
                totalScore -= 12;
            }

            var isStrongMatch =
                candidate.SearchScore >= StrongSearchScore &&
                quality.IsAccepted &&
                quality.CoverageRatio >= StrongCoverageRatio;

            return new CandidateMatch(new LyricsMatch(lyrics, totalScore), isStrongMatch, null);
        }
        catch (OperationCanceledException)
        {
            // Internal fan-out cancellation (or caller cancellation, rethrown by the caller).
            return default;
        }
        catch (Exception exception)
        {
            return new CandidateMatch(null, false, exception);
        }
    }

    private static async Task<IReadOnlyList<LyricLine>?> FetchLyricsAsync(
        HttpClient client,
        long songId,
        CancellationToken cancellationToken)
    {
        using var lyricRequest = CreateRequest($"https://music.163.com/api/song/lyric?id={songId}&lv=1&kv=1&tv=-1");
        using var lyricResponse = await client.SendAsync(
            lyricRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (lyricResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        lyricResponse.EnsureSuccessStatusCode();

        using var lyricDocument = await ReadJsonDocumentAsync(lyricResponse, cancellationToken);

        if (!lyricDocument.RootElement.TryGetProperty("lrc", out var lrcElement) ||
            !lrcElement.TryGetProperty("lyric", out var lyricElement))
        {
            return null;
        }

        var lyricText = lyricElement.GetString();
        return string.IsNullOrWhiteSpace(lyricText)
            ? null
            : LrcParser.Parse(lyricText);
    }

    private static TrackMetadata CreateTrackMetadata(Track track) =>
        new(
            track.DisplayTitle,
            track.Artist,
            track.Album,
            NormalizeSearchText(track.DisplayTitle),
            NormalizeSearchText(track.Artist),
            NormalizeSearchText(track.Album),
            track.DurationSeconds);

    private static string BuildQueryText(params string?[] terms) =>
        string.Join(' ', terms.Where(static term => !string.IsNullOrWhiteSpace(term)));

    private static bool TryParseCandidate(JsonElement songElement, out SongCandidate candidate)
    {
        candidate = default;

        if (!songElement.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var id = idElement.GetInt64();
        var title = songElement.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString() ?? string.Empty
            : string.Empty;
        var alternateTitles = CollectTextValues(songElement, "alia", "tns");
        var artists = songElement.TryGetProperty("ar", out var artistArray)
            ? JoinArtistNames(artistArray)
            : string.Empty;
        var album = songElement.TryGetProperty("al", out var albumElement) &&
                    albumElement.TryGetProperty("name", out var albumNameElement)
            ? albumNameElement.GetString() ?? string.Empty
            : string.Empty;
        var durationSeconds = songElement.TryGetProperty("dt", out var durationElement) &&
                              durationElement.ValueKind == JsonValueKind.Number
            ? Math.Max(0, durationElement.GetInt64() / 1000d)
            : 0;

        candidate = new SongCandidate(id, title, alternateTitles, artists, album, durationSeconds);
        return true;
    }

    private static string JoinArtistNames(JsonElement artistArray)
    {
        if (artistArray.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            ' ',
            artistArray.EnumerateArray()
                .Select(static artist => artist.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString()
                    : null)
                .Where(static name => !string.IsNullOrWhiteSpace(name)));
    }

    private static int ScoreCandidate(TrackMetadata track, SongCandidate candidate)
    {
        var score = 0;
        score += ScoreTitleMatch(track.NormalizedTitle, candidate);
        score += ScoreTextMatch(
            track.NormalizedArtist,
            NormalizeSearchText(candidate.Artist),
            exactScore: 28,
            strongScore: 18,
            fuzzyScore: 10);

        if (!string.IsNullOrWhiteSpace(track.NormalizedAlbum))
        {
            score += ScoreTextMatch(
                track.NormalizedAlbum,
                NormalizeSearchText(candidate.Album),
                exactScore: 14,
                strongScore: 8,
                fuzzyScore: 4);
        }

        if (track.DurationSeconds > 1 && candidate.DurationSeconds > 0)
        {
            var delta = Math.Abs(track.DurationSeconds - candidate.DurationSeconds);
            score += delta switch
            {
                <= 2 => 24,
                <= 5 => 16,
                <= 8 => 10,
                <= 12 => 4,
                >= 25 => -28,
                >= 15 => -12,
                _ => 0
            };
        }

        score -= CalculateVariantPenalty(track.DisplayTitle, candidate);
        return score;
    }

    private static int ScoreTitleMatch(string trackTitle, SongCandidate candidate)
    {
        var bestScore = ScoreTextMatch(
            trackTitle,
            NormalizeSearchText(candidate.Title),
            exactScore: 64,
            strongScore: 46,
            fuzzyScore: 30);

        foreach (var alternateTitle in candidate.AlternateTitles)
        {
            var alternateScore = ScoreTextMatch(
                trackTitle,
                NormalizeSearchText(alternateTitle),
                exactScore: 64,
                strongScore: 46,
                fuzzyScore: 30);

            if (alternateScore > bestScore)
            {
                bestScore = alternateScore;
            }
        }

        return bestScore;
    }

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

        var stripped = DecorationRegex().Replace(value, " ");
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

    private static int CalculateVariantPenalty(string expectedTitle, SongCandidate candidate)
    {
        var expectedMarkers = ExtractVariantMarkers(expectedTitle);
        var candidateMarkers = ExtractVariantMarkers(candidate.Title);

        foreach (var alternateTitle in candidate.AlternateTitles)
        {
            candidateMarkers.UnionWith(ExtractVariantMarkers(alternateTitle));
        }

        var penalty = 0;

        foreach (var marker in candidateMarkers)
        {
            if (!expectedMarkers.Contains(marker))
            {
                penalty += 14;
            }
        }

        foreach (var marker in expectedMarkers)
        {
            if (!candidateMarkers.Contains(marker))
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

    private static string[] CollectTextValues(JsonElement element, params string[] propertyNames)
    {
        var values = new List<string>();

        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var propertyElement) ||
                propertyElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in propertyElement.EnumerateArray())
            {
                var value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    values.Add(value);
                }
            }
        }

        return values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaxJsonResponseBytes)
        {
            throw new HttpRequestException($"NetEase response exceeded the {MaxJsonResponseBytes} byte limit.");
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
                throw new HttpRequestException($"NetEase response exceeded the {MaxJsonResponseBytes} byte limit.");
            }

            buffer.Write(chunk, 0, bytesRead);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
    }

    private static uint ToBigramKey(char first, char second) =>
        ((uint)first << 16) | second;

    private static HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri("https://music.163.com/");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Mozilla", "5.0"));
        return request;
    }

    private readonly record struct TrackMetadata(
        string DisplayTitle,
        string Artist,
        string Album,
        string NormalizedTitle,
        string NormalizedArtist,
        string NormalizedAlbum,
        double DurationSeconds);

    private readonly record struct LyricsMatch(IReadOnlyList<LyricLine> Lyrics, int Score);

    private readonly record struct CandidateMatch(LyricsMatch? Match, bool IsStrongMatch, Exception? Failure);

    private readonly record struct ScoredSongCandidate(long Id, int SearchScore);

    private readonly record struct SongCandidate(
        long Id,
        string Title,
        string[] AlternateTitles,
        string Artist,
        string Album,
        double DurationSeconds);
}
