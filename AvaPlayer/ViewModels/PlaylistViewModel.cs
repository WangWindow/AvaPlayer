using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using AvaPlayer.Models;
using AvaPlayer.Services.Playlist;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.ViewModels;

public partial class TrackItemViewModel : ObservableObject
{
    public TrackItemViewModel(Track track)
    {
        Track = track;
    }

    public Track Track { get; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool IsSelectedForRemoval { get; set; }

    public string Title => Track.DisplayTitle;

    public string Artist => Track.DisplayArtist;

    public string Album => Track.DisplayAlbum;

    public string DurationText => Track.DisplayDuration;

    public bool ShowCurrentGlyph => IsCurrent;

    public bool ShowIdleGlyph => !IsCurrent;

    partial void OnIsCurrentChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCurrentGlyph));
        OnPropertyChanged(nameof(ShowIdleGlyph));
    }
}

public partial class PlaylistViewModel : ObservableObject, IDisposable
{
    private readonly IPlaylistService _playlistService;
    private readonly ILogger<PlaylistViewModel> _logger;
    private readonly Dictionary<string, TrackItemViewModel> _trackCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshScheduled;
    private bool _isUiActive;
    private bool _disposed;
    private bool _syncingPlaylistSelection;

    public PlaylistViewModel(IPlaylistService playlistService, ILogger<PlaylistViewModel> logger)
    {
        _playlistService = playlistService;
        _logger = logger;
        _playlistService.Queue.CollectionChanged += OnQueueCollectionChanged;
        _playlistService.Playlists.CollectionChanged += OnPlaylistsCollectionChanged;
        _playlistService.SelectedPlaylistChanged += OnServiceSelectedPlaylistChanged;
    }

    public ObservableCollection<PlaylistInfo> Playlists => _playlistService.Playlists;

    [ObservableProperty]
    public partial PlaylistInfo? SelectedPlaylist { get; set; }

    [ObservableProperty]
    public partial bool IsRenamingPlaylist { get; set; }

    [ObservableProperty]
    public partial string EditingPlaylistName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyStateText { get; set; } = "还没有歌单，点击 ＋ 添加";

    public bool HasPlaylists => Playlists.Count > 0;

    public bool ShowPlaylistSelector => HasPlaylists && !IsRenamingPlaylist;

    public bool HasSelectedPlaylist => SelectedPlaylist is not null;

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = [];
    public ObservableCollection<TrackItemViewModel> VisibleTracks { get; } = [];

    [ObservableProperty]
    public partial TrackItemViewModel? CurrentTrack { get; set; }

    [ObservableProperty]
    public partial bool HasTracks { get; set; }

    [ObservableProperty]
    public partial bool ShowEmptyState { get; set; } = true;

    [ObservableProperty]
    public partial bool HasVisibleTracks { get; set; }

    [ObservableProperty]
    public partial bool ShowSearchEmptyState { get; set; }

    [ObservableProperty]
    public partial bool IsEditMode { get; set; }

    [ObservableProperty]
    public partial int SelectedTrackCount { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    public event EventHandler<Track>? TrackSelected;
    public Func<Task<string?>>? FolderPickRequested { get; set; }

    public bool ShowNormalToolbar => !IsEditMode;

    public bool ShowEditToolbar => IsEditMode;

    public bool ShowSearchBar => HasTracks;

    public bool ShowClearSearch => !string.IsNullOrWhiteSpace(SearchText);

    public bool CanRemoveSelected => SelectedTrackCount > 0;

    public string EditSelectionText => SelectedTrackCount > 0
        ? $"已选 {SelectedTrackCount} 首"
        : "选择要移除的歌曲";

    public string SearchEmptyStateText => string.IsNullOrWhiteSpace(SearchText)
        ? "未找到匹配歌曲"
        : $"没有匹配“{SearchText.Trim()}”的歌曲";

    [RelayCommand]
    private async Task AddPlaylistAsync()
    {
        if (FolderPickRequested is null)
        {
            return;
        }

        var folderPath = await FolderPickRequested.Invoke();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            _logger.LogWarning("[Playlist] 所选文件夹无法用于本地扫描。");
            return;
        }

        try
        {
            await _playlistService.AddPlaylistAsync(name: string.Empty, folderPath);
            RefreshTracks();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Playlist] 添加歌单失败: {Message}", ex.Message);
        }
    }

    [RelayCommand]
    private void BeginRenamePlaylist()
    {
        if (SelectedPlaylist is null)
        {
            return;
        }

        EditingPlaylistName = SelectedPlaylist.Name;
        IsRenamingPlaylist = true;
    }

    [RelayCommand]
    private async Task ConfirmRenamePlaylistAsync()
    {
        if (SelectedPlaylist is null)
        {
            IsRenamingPlaylist = false;
            return;
        }

        var newName = EditingPlaylistName.Trim();
        if (newName.Length > 0 && newName != SelectedPlaylist.Name)
        {
            try
            {
                await _playlistService.RenamePlaylistAsync(SelectedPlaylist.Id, newName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Playlist] 重命名歌单失败: {Message}", ex.Message);
            }
        }

        IsRenamingPlaylist = false;
    }

    [RelayCommand]
    private void CancelRenamePlaylist() => IsRenamingPlaylist = false;

    [RelayCommand]
    private async Task DeletePlaylistAsync()
    {
        if (SelectedPlaylist is null)
        {
            return;
        }

        try
        {
            await _playlistService.RemovePlaylistAsync(SelectedPlaylist.Id);
            RefreshTracks();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Playlist] 删除歌单失败: {Message}", ex.Message);
        }
    }

    [RelayCommand]
    private void SelectTrack(TrackItemViewModel track)
    {
        if (IsEditMode)
        {
            return;
        }

        TrackSelected?.Invoke(this, track.Track);
    }

    [RelayCommand]
    private void BeginEdit() => IsEditMode = true;

    [RelayCommand]
    private void CancelEdit()
    {
        ClearTrackSelection();
        IsEditMode = false;
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var removedTracks = Tracks
            .Where(static track => track.IsSelectedForRemoval)
            .Select(static track => track.Track)
            .ToArray();

        if (removedTracks.Length == 0)
        {
            return;
        }

        await _playlistService.RemoveTracksAsync(removedTracks);
        ClearTrackSelection();
        IsEditMode = false;
        RefreshTracks();
    }

    public void RefreshFromQueue() => RefreshTracks();

    public void MarkCurrentTrack(Track? track)
    {
        if (!_isUiActive)
        {
            CurrentTrack = null;
            return;
        }

        foreach (var item in Tracks)
        {
            item.IsCurrent = track is not null && item.Track.Id == track.Id;
        }

        CurrentTrack = Tracks.FirstOrDefault(static item => item.IsCurrent);
    }

    public void Activate()
    {
        if (_isUiActive)
        {
            return;
        }

        _isUiActive = true;
        RefreshTracks();
    }

    public void Deactivate()
    {
        if (!_isUiActive && Tracks.Count == 0 && _trackCache.Count == 0)
        {
            return;
        }

        _isUiActive = false;
        IsEditMode = false;
        IsRenamingPlaylist = false;
        CurrentTrack = null;
        ClearTrackSelection();
        ClearTrackCache();
        SearchText = string.Empty;
        HasTracks = false;
        HasVisibleTracks = false;
        ShowEmptyState = true;
        ShowSearchEmptyState = false;
        UpdateSelectedTrackCount();
    }

    partial void OnHasTracksChanged(bool value) => OnPropertyChanged(nameof(ShowSearchBar));

    partial void OnSearchTextChanged(string value)
    {
        if (IsEditMode)
        {
            ClearTrackSelection();
        }

        OnPropertyChanged(nameof(ShowClearSearch));
        OnPropertyChanged(nameof(SearchEmptyStateText));
        RefreshVisibleTracks();
    }

    partial void OnIsEditModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowNormalToolbar));
        OnPropertyChanged(nameof(ShowEditToolbar));

        if (!value)
        {
            ClearTrackSelection();
        }
    }

    private void RefreshTracks()
    {
        if (!_isUiActive)
        {
            return;
        }

        var desiredItems = new List<TrackItemViewModel>(_playlistService.Queue.Count);
        var desiredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in _playlistService.Queue)
        {
            desiredIds.Add(track.Id);
            if (!_trackCache.TryGetValue(track.Id, out var item))
            {
                item = new TrackItemViewModel(track);
                item.PropertyChanged += OnTrackItemPropertyChanged;
                _trackCache[track.Id] = item;
            }

            desiredItems.Add(item);
        }

        foreach (var staleItem in _trackCache
                     .Where(pair => !desiredIds.Contains(pair.Key))
                     .ToArray())
        {
            staleItem.Value.PropertyChanged -= OnTrackItemPropertyChanged;
            _trackCache.Remove(staleItem.Key);
        }

        ApplyTrackOrder(Tracks, desiredItems);
        HasTracks = Tracks.Count > 0;
        RefreshVisibleTracks();
        ShowEmptyState = !HasTracks;
        UpdateSelectedTrackCount();
        MarkCurrentTrack(_playlistService.CurrentTrack);
    }

    private void RefreshVisibleTracks()
    {
        var query = SearchText.Trim();
        var desiredItems = string.IsNullOrWhiteSpace(query)
            ? Tracks.ToArray()
            : Tracks.Where(item => MatchesSearch(item, query)).ToArray();

        ApplyTrackOrder(VisibleTracks, desiredItems);
        HasVisibleTracks = VisibleTracks.Count > 0;
        ShowSearchEmptyState = HasTracks && !HasVisibleTracks && !string.IsNullOrWhiteSpace(query);
    }

    private void OnQueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRefresh();

    private void OnPlaylistsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasPlaylists));
        OnPropertyChanged(nameof(ShowPlaylistSelector));
        UpdateEmptyStateText();
    }

    private void OnServiceSelectedPlaylistChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            SyncSelectedPlaylist();
        }
        else
        {
            Dispatcher.UIThread.Post(SyncSelectedPlaylist, DispatcherPriority.Background);
        }
    }

    private void SyncSelectedPlaylist()
    {
        _syncingPlaylistSelection = true;
        try
        {
            SelectedPlaylist = _playlistService.SelectedPlaylist;
        }
        finally
        {
            _syncingPlaylistSelection = false;
        }
    }

    partial void OnSelectedPlaylistChanged(PlaylistInfo? value)
    {
        OnPropertyChanged(nameof(HasSelectedPlaylist));
        if (!_syncingPlaylistSelection && value is not null && value.Id != _playlistService.SelectedPlaylist?.Id)
        {
            _ = SelectPlaylistSafeAsync(value.Id);
        }
    }

    partial void OnIsRenamingPlaylistChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPlaylistSelector));
    }

    private async Task SelectPlaylistSafeAsync(string playlistId)
    {
        try
        {
            await _playlistService.SelectPlaylistAsync(playlistId);
            RefreshTracks();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Playlist] 切换歌单失败: {Message}", ex.Message);
        }
    }

    private void UpdateEmptyStateText() =>
        EmptyStateText = HasPlaylists ? "这个歌单还没有歌曲" : "还没有歌单，点击 ＋ 添加";

    private void ScheduleRefresh()
    {
        if (!_isUiActive)
        {
            return;
        }

        if (_refreshScheduled)
        {
            return;
        }

        _refreshScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshScheduled = false;
            RefreshTracks();
        }, DispatcherPriority.Background);
    }

    private static bool MatchesSearch(TrackItemViewModel item, string query)
    {
        return item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Artist.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               item.Album.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               Path.GetFileNameWithoutExtension(item.Track.FilePath)
                   .Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyTrackOrder(
        ObservableCollection<TrackItemViewModel> target,
        IReadOnlyList<TrackItemViewModel> desiredItems)
    {
        var desiredSet = desiredItems.ToHashSet();

        for (var index = target.Count - 1; index >= 0; index--)
        {
            if (!desiredSet.Contains(target[index]))
            {
                target.RemoveAt(index);
            }
        }

        for (var index = 0; index < desiredItems.Count; index++)
        {
            var desired = desiredItems[index];
            if (index < target.Count && ReferenceEquals(target[index], desired))
            {
                continue;
            }

            var existingIndex = target.IndexOf(desired);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, index);
            }
            else
            {
                target.Insert(index, desired);
            }
        }
    }

    private void OnTrackItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackItemViewModel.IsSelectedForRemoval))
        {
            UpdateSelectedTrackCount();
        }
    }

    private void ClearTrackSelection()
    {
        foreach (var track in Tracks)
        {
            track.IsSelectedForRemoval = false;
        }
    }

    private void UpdateSelectedTrackCount()
    {
        SelectedTrackCount = Tracks.Count(static track => track.IsSelectedForRemoval);
        OnPropertyChanged(nameof(CanRemoveSelected));
        OnPropertyChanged(nameof(EditSelectionText));
    }

    private void ClearTrackCache()
    {
        foreach (var item in _trackCache.Values)
        {
            item.PropertyChanged -= OnTrackItemPropertyChanged;
        }

        _trackCache.Clear();
        _trackCache.TrimExcess();
        Tracks.Clear();
        VisibleTracks.Clear();
        _refreshScheduled = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _playlistService.Queue.CollectionChanged -= OnQueueCollectionChanged;
        _playlistService.Playlists.CollectionChanged -= OnPlaylistsCollectionChanged;
        _playlistService.SelectedPlaylistChanged -= OnServiceSelectedPlaylistChanged;
        ClearTrackCache();
    }
}
