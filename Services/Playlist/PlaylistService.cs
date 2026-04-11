using System.Collections.ObjectModel;
using AvaPlayer.Models;
using AvaPlayer.Services.Database;

namespace AvaPlayer.Services.Playlist;

public sealed class PlaylistService : IPlaylistService
{
    private readonly IDatabaseService _databaseService;
    private readonly ITrackScannerService _trackScannerService;
    private readonly Random _random = new();
    private PlaybackMode _playbackMode;

    public PlaylistService(IDatabaseService databaseService, ITrackScannerService trackScannerService)
    {
        _databaseService = databaseService;
        _trackScannerService = trackScannerService;
    }

    public ObservableCollection<Track> Queue { get; } = new();

    public Track? CurrentTrack { get; private set; }

    public PlaybackMode PlaybackMode
    {
        get => _playbackMode;
        set
        {
            _playbackMode = value;
            _ = _databaseService.SaveSettingAsync("playback-mode", value.ToString());
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _databaseService.InitializeAsync(cancellationToken);

        var modeSetting = await _databaseService.GetSettingAsync("playback-mode", cancellationToken);
        if (Enum.TryParse<PlaybackMode>(modeSetting, out var playbackMode))
        {
            _playbackMode = playbackMode;
        }

        var tracks = await _databaseService.GetTracksAsync(cancellationToken);
        Queue.Clear();

        foreach (var track in tracks.Where(static track => File.Exists(track.FilePath)))
        {
            Queue.Add(track);
        }

        var currentTrackPath = await _databaseService.GetSettingAsync("current-track-path", cancellationToken);
        if (!string.IsNullOrWhiteSpace(currentTrackPath))
        {
            CurrentTrack = Queue.FirstOrDefault(track =>
                string.Equals(track.FilePath, currentTrackPath, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task AddFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        var tracks = await _trackScannerService.ScanFolderAsync(folderPath, cancellationToken);
        await _databaseService.SaveLibraryFolderAsync(folderPath, cancellationToken);
        await _databaseService.SaveTracksAsync(tracks, cancellationToken);

        var knownPaths = Queue
            .Select(static track => track.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            if (knownPaths.Add(track.FilePath))
            {
                Queue.Add(track);
            }
        }

        SortQueue();

        if (CurrentTrack is null && Queue.Count > 0)
        {
            CurrentTrack = Queue[0];
        }
    }

    public void SetCurrentTrack(Track track)
    {
        CurrentTrack = track;
        _ = _databaseService.SaveSettingAsync("current-track-path", track.FilePath);
    }

    public Track? GetNextTrack()
    {
        if (Queue.Count == 0)
        {
            return null;
        }

        if (CurrentTrack is null)
        {
            return Queue[0];
        }

        return PlaybackMode switch
        {
            PlaybackMode.RepeatOne => CurrentTrack,
            PlaybackMode.Shuffle => GetRandomTrack(CurrentTrack),
            PlaybackMode.RepeatAll => GetWrappedTrack(1),
            PlaybackMode.Sequential => GetSequentialTrack(1),
            _ => GetSequentialTrack(1)
        };
    }

    public Track? GetPreviousTrack()
    {
        if (Queue.Count == 0)
        {
            return null;
        }

        if (CurrentTrack is null)
        {
            return Queue[0];
        }

        return PlaybackMode switch
        {
            PlaybackMode.RepeatOne => CurrentTrack,
            PlaybackMode.Shuffle => GetRandomTrack(CurrentTrack),
            PlaybackMode.RepeatAll => GetWrappedTrack(-1),
            PlaybackMode.Sequential => GetSequentialTrack(-1),
            _ => GetSequentialTrack(-1)
        };
    }

    private Track? GetSequentialTrack(int delta)
    {
        var index = Queue.IndexOf(CurrentTrack!);
        if (index < 0)
        {
            return Queue.Count > 0 ? Queue[0] : null;
        }

        var nextIndex = index + delta;
        return nextIndex >= 0 && nextIndex < Queue.Count
            ? Queue[nextIndex]
            : null;
    }

    private Track GetWrappedTrack(int delta)
    {
        var index = Queue.IndexOf(CurrentTrack!);
        var nextIndex = (index + delta) % Queue.Count;
        if (nextIndex < 0)
        {
            nextIndex += Queue.Count;
        }

        return Queue[nextIndex];
    }

    private Track GetRandomTrack(Track currentTrack)
    {
        if (Queue.Count == 1)
        {
            return currentTrack;
        }

        Track candidate;
        do
        {
            candidate = Queue[_random.Next(Queue.Count)];
        } while (candidate.Id == currentTrack.Id);

        return candidate;
    }

    private void SortQueue()
    {
        var ordered = Queue
            .OrderBy(static track => track.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static track => track.Album, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static track => track.DisplayTitle, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Queue.Clear();
        foreach (var track in ordered)
        {
            Queue.Add(track);
        }
    }
}
