using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvaPlayer.Helpers;
using AvaPlayer.Models;
using AvaPlayer.Services.AlbumArt;
using AvaPlayer.Services.Audio;
using AvaPlayer.Services.Lyrics;
using AvaPlayer.Services.Playlist;
using FluentIcons.Common;

namespace AvaPlayer.ViewModels;

public partial class PlayerBarViewModel : ViewModelBase
{
    private readonly IPlayerService _player;
    private readonly IPlaylistService _playlist;
    private readonly IAlbumArtService _albumArtService;
    private readonly ILyricsService _lyricsService;

    private CancellationTokenSource? _lyricsCts;
    public PlayerBarViewModel(
        IPlayerService player,
        IPlaylistService playlist,
        IAlbumArtService albumArtService,
        ILyricsService lyricsService)
    {
        _player = player;
        _playlist = playlist;
        _albumArtService = albumArtService;
        _lyricsService = lyricsService;

        Lyrics = new LyricsViewModel();
        Volume = _player.Volume;
        PlaybackMode = _playlist.PlaybackMode;
        UpdatePlaybackModeDisplay();

        _player.PlaybackStateChanged += OnPlaybackStateChanged;
        _player.PositionChanged += OnPositionChanged;
        _player.TrackLoaded += OnTrackLoaded;
        _player.TrackEnded += OnTrackEnded;
    }

    [ObservableProperty]
    private IBrush _backgroundBrush = ColorExtractor.DefaultBackground();

    [ObservableProperty]
    private Track? _currentTrack;

    [ObservableProperty]
    private string _titleDisplay = "AvaPlayer";

    [ObservableProperty]
    private string _artistDisplay = "从左上角添加音乐文件夹";

    [ObservableProperty]
    private Bitmap? _albumArtBitmap;

    [ObservableProperty]
    private bool _hasAlbumArt;

    [ObservableProperty]
    private bool _showAlbumArtPlaceholder = true;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private Icon _playPauseIcon = Icon.Play;

    [ObservableProperty]
    private double _position;

    [ObservableProperty]
    private double _duration = 1;

    [ObservableProperty]
    private string _positionText = "0:00";

    [ObservableProperty]
    private string _durationText = "0:00";

    [ObservableProperty]
    private double _volume = 80;

    [ObservableProperty]
    private PlaybackMode _playbackMode = PlaybackMode.Sequential;

    [ObservableProperty]
    private Icon _playbackModeIcon = Icon.ArrowSort;

    [ObservableProperty]
    private string _playbackModeTooltip = "顺序播放";

    [ObservableProperty]
    private bool _isUserSeeking;

    public LyricsViewModel Lyrics { get; }

    public event EventHandler<Track?>? TrackChanged;

    [RelayCommand]
    private void PlayPause()
    {
        if (_player.IsPlaying)
        {
            _player.Pause();
        }
        else
        {
            _player.Resume();
        }
    }

    [RelayCommand]
    private void Pause() => _player.Pause();

    [RelayCommand]
    private void Resume()
    {
        if (CurrentTrack is not null)
        {
            _player.Resume();
            return;
        }

        if (_playlist.CurrentTrack is not null)
        {
            _ = PlayTrackAsync(_playlist.CurrentTrack);
        }
    }

    [RelayCommand]
    private async Task PlayTrackAsync(Track track)
    {
        _playlist.SetCurrentTrack(track);
        await _player.PlayAsync(track.FilePath);
        UpdateTrackInfo(track);
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        var previous = _playlist.GetPreviousTrack();
        if (previous is null)
        {
            return;
        }

        _playlist.SetCurrentTrack(previous);
        await _player.PlayAsync(previous.FilePath);
        UpdateTrackInfo(previous);
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        var next = _playlist.GetNextTrack();
        if (next is null)
        {
            return;
        }

        _playlist.SetCurrentTrack(next);
        await _player.PlayAsync(next.FilePath);
        UpdateTrackInfo(next);
    }

    [RelayCommand]
    private void TogglePlaybackMode()
    {
        PlaybackMode = PlaybackMode switch
        {
            PlaybackMode.Sequential => PlaybackMode.RepeatAll,
            PlaybackMode.RepeatAll => PlaybackMode.RepeatOne,
            PlaybackMode.RepeatOne => PlaybackMode.Shuffle,
            PlaybackMode.Shuffle => PlaybackMode.Sequential,
            _ => PlaybackMode.Sequential
        };

        _playlist.PlaybackMode = PlaybackMode;
        UpdatePlaybackModeDisplay();
    }

    [RelayCommand]
    private void Seek(double seconds) => _player.Seek(seconds);

    partial void OnVolumeChanged(double value)
    {
        if (_player.IsReady)
        {
            _player.Volume = value;
        }
    }

    partial void OnPositionChanged(double value)
    {
        PositionText = FormatTime(value);
    }

    partial void OnDurationChanged(double value)
    {
        DurationText = FormatTime(value);
    }

    private void OnPlaybackStateChanged(object? sender, bool isPlaying)
    {
        IsPlaying = isPlaying;
        PlayPauseIcon = isPlaying ? Icon.Pause : Icon.Play;
    }

    private void OnPositionChanged(object? sender, double position)
    {
        if (IsUserSeeking)
        {
            return;
        }

        Position = position;
        Lyrics.UpdatePosition(position);
    }

    private void OnTrackLoaded(object? sender, EventArgs e)
    {
        Duration = Math.Max(_player.Duration, 1);
    }

    private void OnTrackEnded(object? sender, EventArgs e)
    {
        var next = _playlist.GetNextTrack();
        if (next is null)
        {
            IsPlaying = false;
            PlayPauseIcon = Icon.Play;
            return;
        }

        _ = PlayTrackAsync(next);
    }

    private void UpdateTrackInfo(Track track)
    {
        CurrentTrack = track;
        TitleDisplay = track.DisplayTitle;
        ArtistDisplay = track.DisplayArtistAlbum;
        Position = 0;
        Duration = Math.Max(_player.Duration, 1);

        Lyrics.BeginLoading();
        TrackChanged?.Invoke(this, track);

        _ = LoadAlbumArtAsync(track);
        _ = LoadLyricsAsync(track);
    }

    private async Task LoadAlbumArtAsync(Track track)
    {
        try
        {
            var bitmap = await _albumArtService.GetAlbumArtAsync(track);
            AlbumArtBitmap = bitmap;
            HasAlbumArt = bitmap is not null;
            ShowAlbumArtPlaceholder = bitmap is null;
            BackgroundBrush = bitmap is null
                ? ColorExtractor.DefaultBackground()
                : ColorExtractor.ExtractBackground(bitmap);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AlbumArt] 加载封面失败: {ex.Message}");
            AlbumArtBitmap = null;
            HasAlbumArt = false;
            ShowAlbumArtPlaceholder = true;
            BackgroundBrush = ColorExtractor.DefaultBackground();
        }
    }

    private async Task LoadLyricsAsync(Track track)
    {
        _lyricsCts?.Cancel();
        _lyricsCts?.Dispose();
        _lyricsCts = new CancellationTokenSource();

        try
        {
            var lines = await _lyricsService.GetLyricsAsync(track, _lyricsCts.Token);
            if (_lyricsCts.IsCancellationRequested)
            {
                return;
            }

            if (lines.Count > 0)
            {
                Lyrics.LoadLyrics(lines);
            }
            else
            {
                Lyrics.ClearLyrics();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Lyrics] 加载失败: {ex.Message}");
            Lyrics.ClearLyrics();
        }
    }

    private void UpdatePlaybackModeDisplay()
    {
        (PlaybackModeIcon, PlaybackModeTooltip) = PlaybackMode switch
        {
            PlaybackMode.Sequential => (Icon.ArrowSort, "顺序播放"),
            PlaybackMode.RepeatAll => (Icon.ArrowRepeatAll, "列表循环"),
            PlaybackMode.RepeatOne => (Icon.ArrowRepeat1, "单曲循环"),
            PlaybackMode.Shuffle => (Icon.ArrowShuffle, "随机播放"),
            _ => (Icon.ArrowSort, "顺序播放")
        };
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.Hours > 0
            ? $"{time.Hours}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{time.Minutes}:{time.Seconds:D2}";
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        _lyricsCts?.Cancel();
        _lyricsCts?.Dispose();

        _player.PlaybackStateChanged -= OnPlaybackStateChanged;
        _player.PositionChanged -= OnPositionChanged;
        _player.TrackLoaded -= OnTrackLoaded;
        _player.TrackEnded -= OnTrackEnded;
    }
}
