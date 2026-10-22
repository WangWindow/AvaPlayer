using CommunityToolkit.Mvvm.ComponentModel;
using AvaPlayer.Models;

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
