using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvaPlayer.Helpers;
using AvaPlayer.Models;
using AvaPlayer.Services.AlbumArt;
using AvaPlayer.Services.Audio;
using AvaPlayer.Services.Database;
using AvaPlayer.Services.Lyrics;
using AvaPlayer.Services.Playlist;
using FluentIcons.Common;

namespace AvaPlayer.ViewModels;

public partial class PlayerBarViewModel : ViewModelBase
{
    private const string PlaybackPositionSettingKey = "playback-position-seconds";

    private readonly IDatabaseService _databaseService;
    private readonly IPlayerService _player;
    private readonly IPlaylistService _playlist;
    private readonly IAlbumArtService _albumArtService;
    private readonly ILyricsService _lyricsService;

    private CancellationTokenSource? _lyricsCts;
    public PlayerBarViewModel(
        IPlayerService player,
        IPlaylistService playlist,
        IAlbumArtService albumArtService,
        ILyricsService lyricsService,
        IDatabaseService databaseService)
    {
        _databaseService = databaseService;
        _player = player;
        _playlist = playlist;
        _albumArtService = albumArtService;
        _lyricsService = lyricsService;

        Lyrics = new LyricsViewModel(databaseService);
        Volume = _player.Volume;
        PlaybackMode = _playlist.PlaybackMode;
        UpdatePlaybackModeDisplay();

        _player.PlaybackStateChanged += OnPlaybackStateChanged;
        _player.PositionChanged += OnPositionChanged;
        _player.TrackLoaded += OnTrackLoaded;
        _player.TrackEnded += OnTrackEnded;
        Lyrics.SeekRequested += OnLyricsSeekRequested;
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

    [ObservableProperty]
    private bool _isSettingsVisible;

    public LyricsViewModel Lyrics { get; }

    public event EventHandler<Track?>? TrackChanged;

    public string VolumeText => $"{Math.Round(Volume):0}%";

    public string SettingsToggleText => IsSettingsVisible ? "返回" : "设置";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Lyrics.InitializeAsync(cancellationToken);
        await RestorePlaybackSessionAsync(cancellationToken);
    }

    public Task PersistSessionAsync(CancellationToken cancellationToken = default) =>
        PersistPlaybackPositionAsync(CurrentTrack is null ? 0 : Position, cancellationToken);

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
            _ = TryStartTrackAsync(_playlist.CurrentTrack, "恢复当前曲目");
        }
    }

    [RelayCommand]
    private Task PlayTrackAsync(Track track) => TryStartTrackAsync(track, "播放指定曲目");

    [RelayCommand]
    private async Task PreviousAsync()
    {
        var previous = _playlist.GetPreviousTrack();
        if (previous is null)
        {
            return;
        }

        await TryStartTrackAsync(previous, "切换到上一首");
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        var next = _playlist.GetNextTrack();
        if (next is null)
        {
            return;
        }

        await TryStartTrackAsync(next, "切换到下一首");
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

    [RelayCommand]
    private void ToggleSettings() => IsSettingsVisible = !IsSettingsVisible;

    partial void OnVolumeChanged(double value)
    {
        if (_player.IsReady)
        {
            _player.Volume = value;
        }

        OnPropertyChanged(nameof(VolumeText));
    }

    partial void OnPositionChanged(double value)
    {
        PositionText = FormatTime(value);
    }

    partial void OnDurationChanged(double value)
    {
        DurationText = FormatTime(value);
    }

    partial void OnIsSettingsVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(SettingsToggleText));
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
        Position = Math.Clamp(Position, 0, Duration);
        Lyrics.UpdatePosition(Position);
    }

    private async void OnTrackEnded(object? sender, EventArgs e)
    {
        var next = _playlist.GetNextTrack();
        if (next is null)
        {
            Console.WriteLine("[Player] 当前曲目播放结束，没有可自动切换的下一首。");
            IsPlaying = false;
            PlayPauseIcon = Icon.Play;
            return;
        }

        Console.WriteLine($"[Player] 当前曲目播放结束，准备自动切换到: {next.DisplayTitle}");
        var started = await TryStartTrackAsync(next, "自动切换下一首");
        if (!started)
        {
            IsPlaying = false;
            PlayPauseIcon = Icon.Play;
        }
    }

    private async Task RestorePlaybackSessionAsync(CancellationToken cancellationToken)
    {
        if (_playlist.CurrentTrack is not Track track)
        {
            return;
        }

        var savedPosition = await LoadSavedPlaybackPositionAsync(cancellationToken);

        try
        {
            await _player.PlayAsync(
                track.FilePath,
                startPaused: true,
                startPositionSeconds: savedPosition,
                cancellationToken: cancellationToken);
            UpdateTrackInfo(track, savedPosition);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Player] 恢复播放会话失败: {ex.Message}");
        }
    }

    private void UpdateTrackInfo(Track track, double initialPositionSeconds = 0)
    {
        CurrentTrack = track;
        TitleDisplay = track.DisplayTitle;
        ArtistDisplay = track.DisplayArtistAlbum;
        Duration = Math.Max(Math.Max(track.DurationSeconds, initialPositionSeconds), 1);
        Position = Math.Clamp(initialPositionSeconds, 0, Duration);

        Lyrics.BeginLoading();
        TrackChanged?.Invoke(this, track);
        _ = PersistPlaybackPositionAsync(Position);

        _ = LoadAlbumArtAsync(track);
        _ = LoadLyricsAsync(track);
    }

    private async Task<bool> TryStartTrackAsync(Track track, string reason)
    {
        try
        {
            await _player.PlayAsync(track.FilePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Player] {reason}失败: {track.DisplayTitle} ({track.FilePath}) - {ex.Message}");
            return false;
        }

        _playlist.SetCurrentTrack(track);
        UpdateTrackInfo(track);
        Console.WriteLine($"[Player] {reason}: {track.DisplayTitle}");
        return true;
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
                Lyrics.UpdatePosition(Position);
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

    private void OnLyricsSeekRequested(object? sender, TimeSpan time)
    {
        Position = Math.Clamp(time.TotalSeconds, 0, Duration);
        _player.Seek(time.TotalSeconds);
    }

    private async Task<double> LoadSavedPlaybackPositionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var savedPosition = await _databaseService.GetSettingAsync(PlaybackPositionSettingKey, cancellationToken);
            return double.TryParse(savedPosition, NumberStyles.Float, CultureInfo.InvariantCulture, out var position)
                ? Math.Max(0, position)
                : 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Player] 读取播放进度失败: {ex.Message}");
            return 0;
        }
    }

    private async Task PersistPlaybackPositionAsync(double positionSeconds, CancellationToken cancellationToken = default)
    {
        try
        {
            await _databaseService.SaveSettingAsync(
                PlaybackPositionSettingKey,
                Math.Max(0, positionSeconds).ToString(CultureInfo.InvariantCulture),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Player] 保存播放进度失败: {ex.Message}");
        }
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
        Lyrics.SeekRequested -= OnLyricsSeekRequested;
    }
}
