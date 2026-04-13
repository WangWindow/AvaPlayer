using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvaPlayer.Models;
using AvaPlayer.Services.Playlist;

namespace AvaPlayer.ViewModels;

public partial class TrackItemViewModel : ObservableObject
{
    public TrackItemViewModel(Track track)
    {
        Track = track;
    }

    public Track Track { get; }

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _isSelectedForRemoval;

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

public partial class PlaylistViewModel : ViewModelBase
{
    private readonly IPlaylistService _playlistService;
    private readonly Dictionary<string, TrackItemViewModel> _trackCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _refreshScheduled;
    private bool _isUiActive;

    public PlaylistViewModel(IPlaylistService playlistService)
    {
        _playlistService = playlistService;
        _playlistService.Queue.CollectionChanged += OnQueueCollectionChanged;
    }

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = new();
    public ObservableCollection<TrackItemViewModel> VisibleTracks { get; } = new();

    [ObservableProperty]
    private TrackItemViewModel? _currentTrack;

    [ObservableProperty]
    private bool _hasTracks;

    [ObservableProperty]
    private bool _showEmptyState = true;

    [ObservableProperty]
    private bool _hasVisibleTracks;

    [ObservableProperty]
    private bool _showSearchEmptyState;

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private int _selectedTrackCount;

    [ObservableProperty]
    private string _searchText = string.Empty;

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
    private async Task AddFolderAsync()
    {
        if (FolderPickRequested is null)
        {
            return;
        }

        var folderPath = await FolderPickRequested.Invoke();
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            Console.Error.WriteLine("[Playlist] 所选文件夹无法用于本地扫描。");
            return;
        }

        try
        {
            await _playlistService.AddFolderAsync(folderPath);
            RefreshTracks();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Playlist] 添加文件夹失败: {ex.Message}");
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
        Tracks.Clear();
        VisibleTracks.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        _playlistService.Queue.CollectionChanged -= OnQueueCollectionChanged;
        ClearTrackCache();
    }
}
