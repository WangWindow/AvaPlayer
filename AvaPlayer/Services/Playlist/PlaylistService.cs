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
    private List<Track>? _playbackQueue;
    private string? _playbackPlaylistId;

    private IList<Track> PlaybackQueue => _playbackQueue is not null ? _playbackQueue : Queue;

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

    public async Task AddPlaylistAsync(string name, string folderPath, bool includeSubfolders = true, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        folderPath = FilePaths.Normalize(folderPath);
        var existing = Playlists.FirstOrDefault(p => FilePaths.Comparer.Equals(FilePaths.Normalize(p.FolderPath), folderPath));
        if (existing is not null)
        {
            await RescanPlaylistAsync(existing.Id, existing.IncludeSubfolders, cancellationToken);
            await SelectPlaylistAsync(existing.Id, cancellationToken);
            return;
        }

        var playlistName = ResolvePlaylistName(name, folderPath);
        var playlistId = StableId.ForPath(folderPath);

        var scannedTracks = await _trackScannerService.ScanFolderAsync(folderPath, includeSubfolders, cancellationToken);
        var tracks = scannedTracks
            .Select(track => WithPlaylist(track, playlistId))
            .ToArray();

        await _databaseService.SavePlaylistAsync(new PlaylistInfo
        {
            Id = playlistId,
            Name = playlistName,
            FolderPath = folderPath,
            IncludeSubfolders = includeSubfolders
        }, cancellationToken);
        await _databaseService.SaveTracksAsync(tracks, cancellationToken);

        await ReloadPlaylistsAsync(cancellationToken);
        await SelectPlaylistCoreAsync(
            Playlists.FirstOrDefault(p => p.Id == playlistId),
            persistSelection: true,
            cancellationToken);

        if (CurrentTrack is null && Queue.Count > 0)
        {
            SetCurrentTrack(Queue[0]);
        }
    }

    public async Task RescanPlaylistAsync(string playlistId, bool includeSubfolders, CancellationToken cancellationToken = default)
    {
        var playlist = Playlists.FirstOrDefault(p => p.Id == playlistId)
            ?? throw new ArgumentException("歌单不存在", nameof(playlistId));
        var scanned = await _trackScannerService.ScanFolderAsync(playlist.FolderPath, includeSubfolders, cancellationToken);
        await _databaseService.ReplacePlaylistTracksAsync(playlistId,
            scanned.Select(track => WithPlaylist(track, playlistId)), includeSubfolders, cancellationToken);
        await ReloadPlaylistsAsync(cancellationToken);
        if (_playbackPlaylistId == playlistId)
        {
            var updated = await _databaseService.GetTracksAsync(playlistId, cancellationToken);
            _playbackQueue = OrderTracks(updated.Where(t => File.Exists(t.FilePath))).ToList();
        }
        if (SelectedPlaylist?.Id == playlistId)
        {
            await ReplaceQueueAsync(playlistId, cancellationToken);
            SelectedPlaylistChanged?.Invoke(this, EventArgs.Empty);
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
            .Distinct(FilePaths.Comparer)
            .ToHashSet(FilePaths.Comparer);

        if (removedPaths.Count == 0)
        {
            return;
        }

        await _databaseService.DeleteTracksAsync(removedPaths, cancellationToken, SelectedPlaylist?.Id);

        for (var i = Queue.Count - 1; i >= 0; i--)
        {
            if (removedPaths.Contains(Queue[i].FilePath))
            {
                Queue.RemoveAt(i);
            }
        }

        if (_playbackPlaylistId == SelectedPlaylist?.Id)
            _playbackQueue?.RemoveAll(track => removedPaths.Contains(track.FilePath));

        await ReloadPlaylistsAsync(cancellationToken);
    }

    public void SetCurrentTrack(Track track)
    {
        if (_playbackQueue is null || _playbackPlaylistId != track.PlaylistId)
        {
            _playbackQueue = Queue.ToList();
            _playbackPlaylistId = track.PlaylistId;
        }
        CurrentTrack = track;
    }

    public Track? GetNextTrack()
    {
        if (PlaybackQueue.Count == 0)
        {
            return null;
        }

        if (CurrentTrack is null)
        {
            return PlaybackQueue[0];
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
        if (PlaybackQueue.Count == 0)
        {
            return null;
        }

        if (CurrentTrack is null)
        {
            return PlaybackQueue[0];
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
        IReadOnlyList<Track> tracks = playlistId is null
            ? []
            : await _databaseService.GetTracksAsync(playlistId, cancellationToken);
        Queue.Clear();

        foreach (var track in OrderTracks(tracks.Where(static track => File.Exists(track.FilePath))))
        {
            Queue.Add(track);
        }

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
        SelectedPlaylistChanged?.Invoke(this, EventArgs.Empty);
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

    private int FindCurrentTrackIndex()
    {
        for (var index = 0; index < PlaybackQueue.Count; index++)
            if (FilePaths.Comparer.Equals(PlaybackQueue[index].FilePath, CurrentTrack?.FilePath)) return index;
        return -1;
    }

    private Track? GetSequentialTrack(int delta)
    {
        var index = FindCurrentTrackIndex();
        if (index < 0)
        {
            return PlaybackQueue.Count > 0 ? PlaybackQueue[0] : null;
        }

        var nextIndex = index + delta;
        return nextIndex >= 0 && nextIndex < PlaybackQueue.Count
            ? PlaybackQueue[nextIndex]
            : null;
    }

    private Track GetWrappedTrack(int delta)
    {
        var index = FindCurrentTrackIndex();
        var nextIndex = (index + delta) % PlaybackQueue.Count;
        if (nextIndex < 0)
        {
            nextIndex += PlaybackQueue.Count;
        }

        return PlaybackQueue[nextIndex];
    }

    private Track GetRandomTrack(Track currentTrack)
    {
        if (PlaybackQueue.Count == 1)
        {
            return PlaybackQueue[0];
        }

        Track candidate;
        do
        {
            candidate = PlaybackQueue[_random.Next(PlaybackQueue.Count)];
        } while (candidate.Id == currentTrack.Id);

        return candidate;
    }

    private static IOrderedEnumerable<Track> OrderTracks(IEnumerable<Track> tracks) => tracks
        .OrderBy(static track => track.Artist, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static track => track.Album, StringComparer.OrdinalIgnoreCase)
        .ThenBy(static track => track.DisplayTitle, StringComparer.OrdinalIgnoreCase);
}
