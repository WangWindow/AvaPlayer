using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
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

    public string Title => Track.DisplayTitle;

    public string Artist => Track.DisplayArtist;

    public string DurationText => Track.DisplayDuration;

    public bool ShowCurrentGlyph => IsCurrent;

    public bool ShowDuration => !IsCurrent;

    partial void OnIsCurrentChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowCurrentGlyph));
        OnPropertyChanged(nameof(ShowDuration));
    }
}

public partial class PlaylistViewModel : ViewModelBase
{
    private readonly IPlaylistService _playlistService;

    public PlaylistViewModel(IPlaylistService playlistService)
    {
        _playlistService = playlistService;
        _playlistService.Queue.CollectionChanged += (_, _) => RefreshTracks();
        RefreshTracks();
    }

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = new();

    [ObservableProperty]
    private TrackItemViewModel? _currentTrack;

    [ObservableProperty]
    private bool _hasTracks;

    [ObservableProperty]
    private bool _showEmptyState = true;

    public event EventHandler<Track>? TrackSelected;
    public Func<Task<IStorageFolder?>>? FolderPickRequested { get; set; }

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
        TrackSelected?.Invoke(this, track.Track);
    }

    public void MarkCurrentTrack(Track? track)
    {
        foreach (var item in Tracks)
        {
            item.IsCurrent = track is not null && item.Track.Id == track.Id;
        }

        CurrentTrack = Tracks.FirstOrDefault(static item => item.IsCurrent);
    }

    private void RefreshTracks()
    {
        Tracks.Clear();

        foreach (var track in _playlistService.Queue)
        {
            Tracks.Add(new TrackItemViewModel(track));
        }

        HasTracks = Tracks.Count > 0;
        ShowEmptyState = !HasTracks;
        MarkCurrentTrack(_playlistService.CurrentTrack);
    }
}
