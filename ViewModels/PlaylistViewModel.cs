using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Platform.Storage;
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

    public PlaylistViewModel(IPlaylistService playlistService)
    {
        _playlistService = playlistService;
        _playlistService.Queue.CollectionChanged += OnQueueCollectionChanged;
        RefreshTracks();
    }

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = new();

    [ObservableProperty]
    private TrackItemViewModel? _currentTrack;

    [ObservableProperty]
    private bool _hasTracks;

    [ObservableProperty]
    private bool _showEmptyState = true;

    [ObservableProperty]
    private bool _isEditMode;

    [ObservableProperty]
    private int _selectedTrackCount;

    public event EventHandler<Track>? TrackSelected;
    public Func<Task<IStorageFolder?>>? FolderPickRequested { get; set; }

    public bool ShowNormalToolbar => !IsEditMode;

    public bool ShowEditToolbar => IsEditMode;

    public bool CanRemoveSelected => SelectedTrackCount > 0;

    public string EditSelectionText => SelectedTrackCount > 0
        ? $"已选 {SelectedTrackCount} 首"
        : "选择要移除的歌曲";

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (FolderPickRequested is null)
        {
            return;
        }

        var folder = await FolderPickRequested.Invoke();
        if (folder is null)
        {
            return;
        }

        await _playlistService.AddFolderAsync(folder.Path.LocalPath);
        RefreshTracks();
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
        foreach (var item in Tracks)
        {
            item.IsCurrent = track is not null && item.Track.Id == track.Id;
        }

        CurrentTrack = Tracks.FirstOrDefault(static item => item.IsCurrent);
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

        ApplyTrackOrder(desiredItems);
        HasTracks = Tracks.Count > 0;
        ShowEmptyState = !HasTracks;
        UpdateSelectedTrackCount();
        MarkCurrentTrack(_playlistService.CurrentTrack);
    }

    private void OnQueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
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

    private void ApplyTrackOrder(IReadOnlyList<TrackItemViewModel> desiredItems)
    {
        var desiredSet = desiredItems.ToHashSet();

        for (var index = Tracks.Count - 1; index >= 0; index--)
        {
            if (!desiredSet.Contains(Tracks[index]))
            {
                Tracks.RemoveAt(index);
            }
        }

        for (var index = 0; index < desiredItems.Count; index++)
        {
            var desired = desiredItems[index];
            if (index < Tracks.Count && ReferenceEquals(Tracks[index], desired))
            {
                continue;
            }

            var existingIndex = Tracks.IndexOf(desired);
            if (existingIndex >= 0)
            {
                Tracks.Move(existingIndex, index);
            }
            else
            {
                Tracks.Insert(index, desired);
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

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        _playlistService.Queue.CollectionChanged -= OnQueueCollectionChanged;
        foreach (var item in _trackCache.Values)
        {
            item.PropertyChanged -= OnTrackItemPropertyChanged;
        }
    }
}
