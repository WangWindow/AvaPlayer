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
using AvaPlayer.Services.Network;
using AvaPlayer.Services.Playlist;
using FluentIcons.Common;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.ViewModels;

public partial class PlayerBarViewModel : ViewModelBase
{
    private const string PlaybackPositionSettingKey = "playback-position-seconds";
    private const string VolumeSettingKey = "player-volume";

    private readonly IDatabaseService _databaseService;
    private readonly IPlayerService _player;
    private readonly IPlaylistService _playlist;
    private readonly IAlbumArtService _albumArtService;
    private readonly ILyricsService _lyricsService;
    private readonly INetworkAccessService _networkAccessService;
    private readonly ILogger<PlayerBarViewModel> _logger;

    private CancellationTokenSource? _lyricsCts;
    private CancellationTokenSource? _albumArtCts;
    private bool _isInitialized;
    private bool _isLoadingSettings;
    private bool _isVisualHydrationEnabled = true;
    private int _visualHydrationVersion;
    private string? _hydratedTrackPath;

    public PlayerBarViewModel(
        IPlayerService player,
        IPlaylistService playlist,
        IAlbumArtService albumArtService,
        ILyricsService lyricsService,
        IDatabaseService databaseService,
        INetworkAccessService networkAccessService,
        ILogger<PlayerBarViewModel> logger,
        ILoggerFactory loggerFactory)
    {
        _databaseService = databaseService;
        _player = player;
        _playlist = playlist;
        _albumArtService = albumArtService;
        _lyricsService = lyricsService;
        _networkAccessService = networkAccessService;
        _logger = logger;

        Lyrics = new LyricsViewModel(databaseService, loggerFactory.CreateLogger<LyricsViewModel>());
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
    public partial IBrush BackgroundBrush { get; set; } = ColorExtractor.DefaultBackground();

    [ObservableProperty]
    public partial Track? CurrentTrack { get; set; }

    [ObservableProperty]
    public partial string TitleDisplay { get; set; } = "AvaPlayer";

    [ObservableProperty]
    public partial string ArtistDisplay { get; set; } = "从左上角添加音乐文件夹";

    [ObservableProperty]
    public partial Bitmap? AlbumArtBitmap { get; set; }

    [ObservableProperty]
    public partial bool HasAlbumArt { get; set; }

    [ObservableProperty]
    public partial bool ShowAlbumArtPlaceholder { get; set; } = true;

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial Icon PlayPauseIcon { get; set; } = Icon.Play;

    [ObservableProperty]
    public partial double Position { get; set; }

    [ObservableProperty]
    public partial double Duration { get; set; } = 1;

    [ObservableProperty]
    public partial string PositionText { get; set; } = "0:00";

    [ObservableProperty]
    public partial string DurationText { get; set; } = "0:00";

    [ObservableProperty]
    public partial double Volume { get; set; } = 80;

    [ObservableProperty]
    public partial PlaybackMode PlaybackMode { get; set; } = PlaybackMode.Sequential;

    [ObservableProperty]
    public partial Icon PlaybackModeIcon { get; set; } = Icon.ArrowSort;

    [ObservableProperty]
    public partial string PlaybackModeTooltip { get; set; } = "顺序播放";

    [ObservableProperty]
    public partial bool IsUserSeeking { get; set; }

    [ObservableProperty]
    public partial bool IsSettingsVisible { get; set; }

    [ObservableProperty]
    public partial bool IsNetworkEnabled { get; set; } = true;

    public LyricsViewModel Lyrics { get; }

    public event EventHandler<Track?>? TrackChanged;

    public string VolumeText => $"{Math.Round(Volume):0}%";

    public Icon VolumeIcon => Volume switch
    {
        <= 0.5 => Icon.SpeakerMute,
        < 30 => Icon.Speaker0,
        < 70 => Icon.Speaker1,
        _ => Icon.Speaker2
    };

    public Icon SettingsToggleIcon => IsSettingsVisible ? Icon.ChevronLeft : Icon.Settings;

    public string SettingsToggleToolTip => IsSettingsVisible ? "返回播放界面" : "打开设置";

    public async Task InitializeAsync(bool hydrateVisuals = true, CancellationToken cancellationToken = default)
    {
        if (!hydrateVisuals)
        {
            SuspendVisualHydration();
        }
        else
        {
            _isVisualHydrationEnabled = true;
        }

        if (_isInitialized)
        {
            SyncStateFromServices();
            if (hydrateVisuals)
            {
                await EnsureVisualHydrationAsync(cancellationToken);
            }

            return;
        }

        SyncStateFromServices();
        await Lyrics.InitializeAsync(cancellationToken);
        await RestoreVolumeAsync(cancellationToken);
        await RestorePlaybackSessionAsync(cancellationToken);
        _isInitialized = true;
    }

    public Task EnsureVisualHydrationAsync(CancellationToken cancellationToken = default)
    {
        _isVisualHydrationEnabled = true;

        if (CurrentTrack is null)
        {
            return Task.CompletedTask;
        }

        if (string.Equals(_hydratedTrackPath, CurrentTrack.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        StartVisualHydration(CurrentTrack, cancellationToken);
        return Task.CompletedTask;
    }

    public void SuspendVisualHydration()
    {
        _isVisualHydrationEnabled = false;
        Interlocked.Increment(ref _visualHydrationVersion);
        _hydratedTrackPath = null;
        CancelAlbumArtLoading();
        CancelLyricsLoading();
        ReplaceAlbumArt(null);
        HasAlbumArt = false;
        ShowAlbumArtPlaceholder = true;
        BackgroundBrush = ColorExtractor.DefaultBackground();
        Lyrics.ClearLyrics();
    }

    public Task PersistSessionAsync(CancellationToken cancellationToken = default) =>
        Task.WhenAll(
            PersistPlaybackPositionAsync(CurrentTrack is null ? 0 : Position, cancellationToken),
            PersistVolumeAsync(Volume, cancellationToken)
        );

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
        OnPropertyChanged(nameof(VolumeIcon));
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
        OnPropertyChanged(nameof(SettingsToggleIcon));
        OnPropertyChanged(nameof(SettingsToggleToolTip));
    }

    partial void OnIsNetworkEnabledChanged(bool value)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        _networkAccessService.IsNetworkEnabled = value;
        _ = _networkAccessService.PersistAsync();
    }

    partial void OnPlaybackModeChanged(PlaybackMode value) => UpdatePlaybackModeDisplay();

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
        if (_isVisualHydrationEnabled)
        {
            Lyrics.UpdatePosition(position);
        }
    }

    private void OnTrackLoaded(object? sender, EventArgs e)
    {
        Duration = Math.Max(_player.Duration, 1);
        Position = Math.Clamp(Position, 0, Duration);
        if (_isVisualHydrationEnabled)
        {
            Lyrics.UpdatePosition(Position);
        }
    }

    private async void OnTrackEnded(object? sender, EventArgs e)
    {
        var next = _playlist.GetNextTrack();
        if (next is null)
        {
            _logger.LogInformation("[Player] 当前曲目播放结束，没有可自动切换的下一首。");
            IsPlaying = false;
            PlayPauseIcon = Icon.Play;
            return;
        }

        _logger.LogInformation("[Player] 当前曲目播放结束，准备自动切换到: {Title}", next.DisplayTitle);
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
            _logger.LogError(ex, "[Player] 恢复播放会话失败: {Message}", ex.Message);
        }
    }

    private void UpdateTrackInfo(Track track, double initialPositionSeconds = 0)
    {
        CurrentTrack = track;
        TitleDisplay = track.DisplayTitle;
        ArtistDisplay = track.DisplayArtistAlbum;
        Duration = Math.Max(Math.Max(track.DurationSeconds, initialPositionSeconds), 1);
        Position = Math.Clamp(initialPositionSeconds, 0, Duration);

        TrackChanged?.Invoke(this, track);
        _ = PersistPlaybackPositionAsync(Position);

        if (_isVisualHydrationEnabled)
        {
            StartVisualHydration(track);
        }
        else
        {
            _hydratedTrackPath = null;
            Lyrics.ClearLyrics();
            ReplaceAlbumArt(null);
            HasAlbumArt = false;
            ShowAlbumArtPlaceholder = true;
            BackgroundBrush = ColorExtractor.DefaultBackground();
        }
    }

    private async Task<bool> TryStartTrackAsync(Track track, string reason)
    {
        try
        {
            await _player.PlayAsync(track.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Player] {Reason}失败: {Title} ({FilePath}) - {Message}", reason, track.DisplayTitle, track.FilePath, ex.Message);
            return false;
        }

        _playlist.SetCurrentTrack(track);
        UpdateTrackInfo(track);
        _logger.LogInformation("[Player] {Reason}: {Title}", reason, track.DisplayTitle);
        return true;
    }

    private void StartVisualHydration(Track track, CancellationToken cancellationToken = default)
    {
        if (!_isVisualHydrationEnabled)
        {
            return;
        }

        var hydrationVersion = Interlocked.Increment(ref _visualHydrationVersion);
        _hydratedTrackPath = track.FilePath;
        CancelAlbumArtLoading();
        _albumArtCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        Lyrics.BeginLoading();
        _ = LoadAlbumArtAsync(track, hydrationVersion, _albumArtCts.Token);
        _ = LoadLyricsAsync(track, hydrationVersion, cancellationToken);
    }

    private async Task LoadAlbumArtAsync(Track track, int hydrationVersion, CancellationToken cancellationToken = default)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await _albumArtService.GetAlbumArtAsync(track, cancellationToken);
            if (!CanApplyVisualResult(track, hydrationVersion))
            {
                bitmap?.Dispose();
                return;
            }

            ReplaceAlbumArt(bitmap);
            HasAlbumArt = bitmap is not null;
            ShowAlbumArtPlaceholder = bitmap is null;
            BackgroundBrush = bitmap is null
                ? ColorExtractor.DefaultBackground()
                : ColorExtractor.ExtractBackground(bitmap);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            bitmap?.Dispose();
        }
        catch (Exception ex)
        {
            bitmap?.Dispose();
            if (!CanApplyVisualResult(track, hydrationVersion))
            {
                return;
            }

            _logger.LogError(ex, "[AlbumArt] 加载封面失败: {Message}", ex.Message);
            ReplaceAlbumArt(null);
            HasAlbumArt = false;
            ShowAlbumArtPlaceholder = true;
            BackgroundBrush = ColorExtractor.DefaultBackground();
        }
    }

    private async Task LoadLyricsAsync(Track track, int hydrationVersion, CancellationToken cancellationToken = default)
    {
        CancelLyricsLoading();
        _lyricsCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        var lyricsToken = _lyricsCts.Token;

        try
        {
            var lines = await _lyricsService.GetLyricsAsync(track, lyricsToken);
            if (lyricsToken.IsCancellationRequested || !CanApplyVisualResult(track, hydrationVersion))
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
        catch (OperationCanceledException) when (lyricsToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!CanApplyVisualResult(track, hydrationVersion))
            {
                return;
            }

            _logger.LogError(ex, "[Lyrics] 加载失败: {Message}", ex.Message);
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
            _logger.LogError(ex, "[Player] 读取播放进度失败: {Message}", ex.Message);
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
            _logger.LogError(ex, "[Player] 保存播放进度失败: {Message}", ex.Message);
        }
    }

    private async Task RestoreVolumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var savedVolume = await _databaseService.GetSettingAsync(VolumeSettingKey, cancellationToken);
            if (double.TryParse(savedVolume, NumberStyles.Float, CultureInfo.InvariantCulture, out var volume))
            {
                Volume = Math.Clamp(volume, 0, 100);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Player] 读取音量设置失败: {Message}", ex.Message);
        }
    }

    private async Task PersistVolumeAsync(double volume, CancellationToken cancellationToken = default)
    {
        try
        {
            await _databaseService.SaveSettingAsync(
                VolumeSettingKey,
                volume.ToString(CultureInfo.InvariantCulture),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Player] 保存音量设置失败: {Message}", ex.Message);
        }
    }

    private bool CanApplyVisualResult(Track track, int hydrationVersion) =>
        _isVisualHydrationEnabled &&
        CurrentTrack is not null &&
        string.Equals(CurrentTrack.FilePath, track.FilePath, StringComparison.OrdinalIgnoreCase) &&
        hydrationVersion == _visualHydrationVersion;

    private void ReplaceAlbumArt(Bitmap? bitmap)
    {
        var previous = AlbumArtBitmap;
        AlbumArtBitmap = bitmap;
        if (previous is not null && !ReferenceEquals(previous, bitmap))
        {
            previous.Dispose();
        }
    }

    private void CancelLyricsLoading()
    {
        _lyricsCts?.Cancel();
        _lyricsCts?.Dispose();
        _lyricsCts = null;
    }

    private void CancelAlbumArtLoading()
    {
        _albumArtCts?.Cancel();
        _albumArtCts?.Dispose();
        _albumArtCts = null;
    }

    private void SyncStateFromServices()
    {
        _isLoadingSettings = true;
        try
        {
            IsNetworkEnabled = _networkAccessService.IsEnabled;
            PlaybackMode = _playlist.PlaybackMode;
            UpdatePlaybackModeDisplay();
        }
        finally
        {
            _isLoadingSettings = false;
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

        CancelLyricsLoading();
        CancelAlbumArtLoading();
        ReplaceAlbumArt(null);

        _player.PlaybackStateChanged -= OnPlaybackStateChanged;
        _player.PositionChanged -= OnPositionChanged;
        _player.TrackLoaded -= OnTrackLoaded;
        _player.TrackEnded -= OnTrackEnded;
        Lyrics.SeekRequested -= OnLyricsSeekRequested;
    }
}
