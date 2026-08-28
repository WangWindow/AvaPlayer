using System.Collections.ObjectModel;
using AvaPlayer.Helpers;
using AvaPlayer.Models;
using AvaPlayer.Services.Database;

namespace AvaPlayer.Services.Playlist;

public sealed class PlaylistService : IPlaylistService
{
    private readonly IDatabaseService _databaseService;
    private readonly ITrackScannerService _trackScannerService;
    private readonly Random _random = new();
    private PlaybackMode _playbackMode;
    private bool _isLoading;

    public PlaylistService(IDatabaseService databaseService, ITrackScannerService trackScannerService)
    {
        _databaseService = databaseService;
        _trackScannerService = trackScannerService;
    }

    public ObservableCollection<Track> Queue { get; } = new();

    public ObservableCollection<PlaylistInfo> Playlists { get; } = new();

    public PlaylistInfo? SelectedPlaylist { get; private set; }

    public event EventHandler? SelectedPlaylistChanged;

    public Track? CurrentTrack { get; private set; }

    public PlaybackMode PlaybackMode
    {
        get => _playbackMode;
        set
        {
            if (_playbackMode == value)
            {
                return;
            }

            _playbackMode = value;
            if (!_isLoading)
            {
                _ = _databaseService.SaveSettingAsync("playback-mode", value.ToString());
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _isLoading = true;
        try
        {
            await _databaseService.InitializeAsync(cancellationToken);

            var modeSetting = await _databaseService.GetSettingAsync("playback-mode", cancellationToken);
            if (Enum.TryParse<PlaybackMode>(modeSetting, out var playbackMode))
            {
                PlaybackMode = playbackMode;
            }

            await ReloadPlaylistsAsync(cancellationToken);

            var selectedIdSetting = await _databaseService.GetSettingAsync("selected-playlist-id", cancellationToken);
            var selected = Playlists.FirstOrDefault(p => p.Id == selectedIdSetting) ?? Playlists.FirstOrDefault();
            await SelectPlaylistCoreAsync(selected, persistSelection: false, cancellationToken);
        }
        finally
        {
            _isLoading = false;
        }
    }

    public async Task AddPlaylistAsync(string name, string folderPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var playlistName = ResolvePlaylistName(name, folderPath);
        var playlistId = StableId.ForPath(folderPath);

        var scannedTracks = await _trackScannerService.ScanFolderAsync(folderPath, cancellationToken);
        var tracks = scannedTracks
            .Select(track => WithPlaylist(track, playlistId))
            .ToArray();

        await _databaseService.SavePlaylistAsync(new PlaylistInfo
        {
            Id = playlistId,
            Name = playlistName,
            FolderPath = folderPath
        }, cancellationToken);
        await _databaseService.SaveTracksAsync(tracks, cancellationToken);

        await ReloadPlaylistsAsync(cancellationToken);
        await SelectPlaylistCoreAsync(
            Playlists.FirstOrDefault(p => p.Id == playlistId),
            persistSelection: true,
            cancellationToken);

        if (CurrentTrack is null && Queue.Count > 0)
        {
            CurrentTrack = Queue[0];
        }
    }

    public async Task RenamePlaylistAsync(string playlistId, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        await _databaseService.RenamePlaylistAsync(playlistId, newName.Trim(), cancellationToken);
        await ReloadPlaylistsAsync(cancellationToken);
    }

    public async Task RemovePlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);

        var wasSelected = SelectedPlaylist?.Id == playlistId;
        await _databaseService.DeletePlaylistAsync(playlistId, cancellationToken);
        await ReloadPlaylistsAsync(cancellationToken);

        if (wasSelected)
        {
            await SelectPlaylistCoreAsync(Playlists.FirstOrDefault(), persistSelection: true, cancellationToken);
        }
    }

    public async Task SelectPlaylistAsync(string? playlistId, CancellationToken cancellationToken = default)
    {
        var target = playlistId is null
            ? null
            : Playlists.FirstOrDefault(p => p.Id == playlistId);
        await SelectPlaylistCoreAsync(target, persistSelection: true, cancellationToken);
    }

    public async Task RemoveTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default)
    {
        var removedPaths = tracks
            .Select(static track => track.FilePath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (removedPaths.Count == 0)
        {
            return;
        }

        await _databaseService.DeleteTracksAsync(removedPaths, cancellationToken);

        for (var i = Queue.Count - 1; i >= 0; i--)
        {
            if (removedPaths.Contains(Queue[i].FilePath))
            {
                Queue.RemoveAt(i);
            }
        }

        if (CurrentTrack is not null && removedPaths.Contains(CurrentTrack.FilePath))
        {
            CurrentTrack = null;
        }

        await ReloadPlaylistsAsync(cancellationToken);
    }

    public void SetCurrentTrack(Track track)
    {
        CurrentTrack = track;
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

    private async Task SelectPlaylistCoreAsync(PlaylistInfo? playlist, bool persistSelection,
        CancellationToken cancellationToken)
    {
        SelectedPlaylist = playlist;

        if (persistSelection && !_isLoading)
        {
            await _databaseService.SaveSettingAsync("selected-playlist-id", playlist?.Id ?? string.Empty,
                cancellationToken);
        }

        await ReplaceQueueAsync(playlist?.Id, cancellationToken);
        SelectedPlaylistChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReplaceQueueAsync(string? playlistId, CancellationToken cancellationToken)
    {
        var tracks = await _databaseService.GetTracksAsync(playlistId, cancellationToken);
        Queue.Clear();

        foreach (var track in tracks.Where(static track => File.Exists(track.FilePath)))
        {
            Queue.Add(track);
        }

        SortQueue();
        CurrentTrack = null;
    }

    private async Task ReloadPlaylistsAsync(CancellationToken cancellationToken)
    {
        var playlists = await _databaseService.GetPlaylistsAsync(cancellationToken);

        Playlists.Clear();
        foreach (var playlist in playlists)
        {
            Playlists.Add(playlist);
        }

        if (SelectedPlaylist is not null)
        {
            SelectedPlaylist = Playlists.FirstOrDefault(p => p.Id == SelectedPlaylist.Id);
        }
    }

    private static string ResolvePlaylistName(string name, string folderPath)
    {
        var trimmed = name.Trim();
        if (trimmed.Length > 0)
        {
            return trimmed;
        }

        var folderName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(folderName) ? folderPath : folderName;
    }

    private static Track WithPlaylist(Track track, string playlistId) => new()
    {
        Id = track.Id,
        FilePath = track.FilePath,
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album,
        DurationSeconds = track.DurationSeconds,
        PlaylistId = playlistId
    };

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
