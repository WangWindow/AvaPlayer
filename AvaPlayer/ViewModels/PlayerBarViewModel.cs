using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvaPlayer.Helpers;
using AvaPlayer.Models;
using AvaPlayer.Services.AlbumArt;
using AvaPlayer.Services.Lyrics;
using AvaPlayer.Services.PlaybackSession;
using AvaPlayer.Services.Settings;
using FluentIcons.Common;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.ViewModels;

public partial class PlayerBarViewModel : ObservableObject, IDisposable
{
    private const string VolumeSettingKey = "player-volume";

    private readonly ISettingsService _settings;
    private readonly IAlbumArtService _albumArtService;
    private readonly ILyricsService _lyricsService;
    private readonly ILyricPresentationService _lyricPresentation;
    private readonly IPlaybackSessionClient _sessionClient;
    private readonly ILogger<PlayerBarViewModel> _logger;
    private IDisposable? _snapshotSubscription;

    private CancellationTokenSource? _lyricsCts;
    private CancellationTokenSource? _albumArtCts;
    private bool _isInitialized;
    private bool _isVisualHydrationEnabled = true;
    private int _visualHydrationVersion;
    private string? _hydratedTrackPath;
    private bool _disposed;

    public PlayerBarViewModel(
        IAlbumArtService albumArtService,
        ILyricsService lyricsService,
        ISettingsService settings,
        ILyricPresentationService lyricPresentation,
        IPlaybackSessionClient sessionClient,
        ILogger<PlayerBarViewModel> logger)
    {
        _settings = settings;
        _albumArtService = albumArtService;
        _lyricsService = lyricsService;
        _lyricPresentation = lyricPresentation;
        _sessionClient = sessionClient;
        _logger = logger;

        _snapshotSubscription = _sessionClient.Subscribe(ApplySnapshot);
        _lyricPresentation.SeekRequested += OnLyricsSeekRequested;
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
    public partial double CoverSize { get; set; } = 260;

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
            if (hydrateVisuals)
            {
                await EnsureVisualHydrationAsync(cancellationToken);
            }

            return;
        }

        await RestoreVolumeAsync(cancellationToken);
        await _sessionClient.RestorePlaybackAtPositionAsync(
            await _sessionClient.GetSavedPositionAsync(cancellationToken),
            cancellationToken);
        _isInitialized = true;
    }

    public Task EnsureVisualHydrationAsync(CancellationToken cancellationToken = default)
    {
        _isVisualHydrationEnabled = true;

        if (CurrentTrack is null)
        {
            return Task.CompletedTask;
        }

        RefreshDisplayState();

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
        _lyricPresentation.ClearLyrics();
        TitleDisplay = "AvaPlayer";
        ArtistDisplay = "从左上角添加音乐文件夹";
        PositionText = "0:00";
        DurationText = "0:00";
        CoverSize = 260;
    }

    [RelayCommand]
    private Task PlayPauseAsync() => _sessionClient.TogglePlayPauseAsync();

    [RelayCommand]
    private Task PauseAsync() => _sessionClient.PauseAsync();

    [RelayCommand]
    private void Resume() => _ = _sessionClient.ResumeAsync();

    [RelayCommand]
    private Task PlayTrackAsync(Track track) => _sessionClient.PlayTrackAsync(track);

    [RelayCommand]
    private Task PreviousAsync() => _sessionClient.PreviousAsync();

    [RelayCommand]
    private Task NextAsync() => _sessionClient.NextAsync();

    [RelayCommand]
    private void TogglePlaybackMode() => _ = _sessionClient.CyclePlaybackModeAsync();

    [RelayCommand]
    private Task SeekAsync(double seconds) => _sessionClient.SeekAsync(seconds);

    [RelayCommand]
    private void ToggleSettings() => IsSettingsVisible = !IsSettingsVisible;

    partial void OnVolumeChanged(double value)
    {
        _ = _sessionClient.SetVolumeAsync(value);
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

    partial void OnPlaybackModeChanged(PlaybackMode value) => UpdatePlaybackModeDisplay();

    private async void OnLyricsSeekRequested(object? sender, TimeSpan time)
    {
        var seconds = Math.Clamp(time.TotalSeconds, 0, Duration);

        if (!IsPlaying)
        {
            await _sessionClient.ResumeAsync();
        }

        await _sessionClient.SeekAsync(seconds);
    }

    private async Task RestoreVolumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var savedVolume = await _settings.GetAsync(VolumeSettingKey, cancellationToken);
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

    private void UpdateTrackInfo(Track track)
    {
        CurrentTrack = track;
        TitleDisplay = track.DisplayTitle;
        ArtistDisplay = track.DisplayArtistAlbum;
        Duration = Math.Max(Math.Max(track.DurationSeconds, Position), 1);
        Position = Math.Clamp(Position, 0, Duration);

        TrackChanged?.Invoke(this, track);

        if (_isVisualHydrationEnabled)
        {
            StartVisualHydration(track);
        }
        else
        {
            _hydratedTrackPath = null;
            _lyricPresentation.ClearLyrics();
            ReplaceAlbumArt(null);
            HasAlbumArt = false;
            ShowAlbumArtPlaceholder = true;
            BackgroundBrush = ColorExtractor.DefaultBackground();
        }
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
        _lyricPresentation.BeginLoading();
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
                _lyricPresentation.LoadLyrics(lines);
                _lyricPresentation.UpdatePosition(Position);
            }
            else
            {
                _lyricPresentation.ClearLyrics();
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
            _lyricPresentation.ClearLyrics();
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

    private void RefreshDisplayState()
    {
        if (CurrentTrack is not { } track)
        {
            return;
        }

        TitleDisplay = track.DisplayTitle;
        ArtistDisplay = track.DisplayArtistAlbum;
        Duration = Math.Max(Math.Max(track.DurationSeconds, Position), 1);
        Position = Math.Clamp(Position, 0, Duration);
        CoverSize = 260;
    }

    private void ApplySnapshot(PlaybackSnapshot snapshot)
    {
        if (CurrentTrack is null && snapshot.CurrentTrack is { } track)
        {
            UpdateTrackInfo(track);
        }
        else if (snapshot.CurrentTrack is { } t &&
                 !string.Equals(t.FilePath, CurrentTrack?.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            UpdateTrackInfo(t);
        }

        IsPlaying = snapshot.IsPlaying;
        PlayPauseIcon = snapshot.IsPlaying ? Icon.Pause : Icon.Play;

        if (!IsUserSeeking)
        {
            Position = snapshot.Position;
        }

        Duration = snapshot.Duration;
        Volume = snapshot.Volume;
        PlaybackMode = snapshot.PlaybackMode;
        UpdatePlaybackModeDisplay();

        // Forward the authoritative position to lyric presentation so highlighting
        // and auto-scroll follow playback (including after session restore).
        // LyricsViewModel.UpdatePosition guards on Lines.Count == 0, so this is a
        // safe no-op when no lyrics are loaded.
        if (!IsUserSeeking)
        {
            _lyricPresentation.UpdatePosition(Position);
        }
    }

    private static string FormatTime(double seconds) => DurationFormatter.Format(seconds);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _snapshotSubscription?.Dispose();
        CancelLyricsLoading();
        CancelAlbumArtLoading();
        ReplaceAlbumArt(null);

        _lyricPresentation.SeekRequested -= OnLyricsSeekRequested;
    }
}
