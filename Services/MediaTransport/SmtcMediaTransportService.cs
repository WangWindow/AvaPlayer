#if WINDOWS_SMTC
using AvaPlayer.Models;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;

namespace AvaPlayer.Services.MediaTransport;

public sealed class SmtcMediaTransportService : IMediaTransportService
{
    private static readonly TimeSpan TimelineUpdateInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private MediaPlayer? _mediaPlayer;
    private SystemMediaTransportControls? _controls;
    private Track? _currentTrack;
    private bool _isPlaying;
    private TimeSpan _position;
    private TimeSpan _duration;
    private TimeSpan _lastTimelinePosition;
    private DateTimeOffset _lastTimelineUpdate = DateTimeOffset.MinValue;
    private PlaybackMode _playbackMode = PlaybackMode.Sequential;
    private bool _initialized;

    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler<TimeSpan>? SeekRequested;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized || !OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        try
        {
            _mediaPlayer = new MediaPlayer();
            _mediaPlayer.CommandManager.IsEnabled = false;

            _controls = _mediaPlayer.SystemMediaTransportControls;
            _controls.IsEnabled = true;
            _controls.IsPlayEnabled = true;
            _controls.IsPauseEnabled = true;
            _controls.IsNextEnabled = true;
            _controls.IsPreviousEnabled = true;
            _controls.ButtonPressed += OnButtonPressed;
            _controls.PlaybackPositionChangeRequested += OnPlaybackPositionChangeRequested;

            ApplyPlaybackMode();
            ApplyPlaybackStatus();
            UpdateTimelineProperties(force: true);

            _initialized = true;
            Console.WriteLine("[SMTC] 服务已注册");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SMTC] 初始化失败: {ex.Message}");
            DisposePlayer();
        }

        return Task.CompletedTask;
    }

    public async Task UpdateTrackAsync(Track? track, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _currentTrack = track;
            _position = TimeSpan.Zero;
            _duration = track is null
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(Math.Max(0, track.DurationSeconds));
        }

        if (_controls is null)
        {
            return;
        }

        try
        {
            await UpdateDisplayAsync(track, cancellationToken).ConfigureAwait(false);
            ApplyPlaybackStatus();
            UpdateTimelineProperties(force: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SMTC] 更新媒体信息失败: {ex.Message}");
        }
    }

    public void UpdatePlaybackState(bool isPlaying)
    {
        lock (_gate)
        {
            _isPlaying = isPlaying;
        }

        ApplyPlaybackStatus();
        UpdateTimelineProperties(force: true);
    }

    public void UpdatePosition(TimeSpan position, TimeSpan duration)
    {
        lock (_gate)
        {
            _position = position;
            _duration = duration > TimeSpan.Zero ? duration : _duration;
        }

        UpdateTimelineProperties(force: false);
    }

    public void UpdatePlaybackMode(PlaybackMode playbackMode)
    {
        lock (_gate)
        {
            _playbackMode = playbackMode;
        }

        ApplyPlaybackMode();
    }

    public void Dispose()
    {
        DisposePlayer();
    }

    private async Task UpdateDisplayAsync(Track? track, CancellationToken cancellationToken)
    {
        var controls = _controls;
        if (controls is null)
        {
            return;
        }

        var updater = controls.DisplayUpdater;
        updater.ClearAll();

        if (track is null)
        {
            updater.Type = MediaPlaybackType.Music;
            updater.Update();
            return;
        }

        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(track.FilePath);
            await updater.CopyFromFileAsync(MediaPlaybackType.Music, storageFile);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SMTC] 读取文件元数据失败: {ex.Message}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = track.DisplayTitle;
        updater.MusicProperties.Artist = track.DisplayArtist;
        updater.MusicProperties.AlbumArtist = track.DisplayArtist;
        updater.MusicProperties.AlbumTitle = track.DisplayAlbum;
        updater.Update();
    }

    private void ApplyPlaybackStatus()
    {
        var controls = _controls;
        if (controls is null)
        {
            return;
        }

        controls.PlaybackStatus = GetPlaybackStatus();
    }

    private MediaPlaybackStatus GetPlaybackStatus()
    {
        lock (_gate)
        {
            if (_currentTrack is null)
            {
                return MediaPlaybackStatus.Stopped;
            }

            return _isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
    }

    private void ApplyPlaybackMode()
    {
        var controls = _controls;
        if (controls is null)
        {
            return;
        }

        lock (_gate)
        {
            controls.AutoRepeatMode = _playbackMode switch
            {
                PlaybackMode.RepeatAll => MediaPlaybackAutoRepeatMode.List,
                PlaybackMode.RepeatOne => MediaPlaybackAutoRepeatMode.Track,
                _ => MediaPlaybackAutoRepeatMode.None
            };
            controls.ShuffleEnabled = _playbackMode == PlaybackMode.Shuffle;
        }
    }

    private void UpdateTimelineProperties(bool force)
    {
        var controls = _controls;
        if (controls is null)
        {
            return;
        }

        Track? currentTrack;
        TimeSpan position;
        TimeSpan duration;

        lock (_gate)
        {
            currentTrack = _currentTrack;
            position = _position;
            duration = _duration;
        }

        if (currentTrack is null)
        {
            if (!force)
            {
                return;
            }

            controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = TimeSpan.Zero,
                MaxSeekTime = TimeSpan.Zero,
                EndTime = TimeSpan.Zero
            });
            _lastTimelineUpdate = DateTimeOffset.UtcNow;
            _lastTimelinePosition = TimeSpan.Zero;
            return;
        }

        if (duration <= TimeSpan.Zero)
        {
            duration = TimeSpan.FromSeconds(Math.Max(0, currentTrack.DurationSeconds));
        }

        position = ClampPosition(position, duration);
        var now = DateTimeOffset.UtcNow;
        if (!force &&
            now - _lastTimelineUpdate < TimelineUpdateInterval &&
            Math.Abs((position - _lastTimelinePosition).TotalSeconds) < TimelineUpdateInterval.TotalSeconds)
        {
            return;
        }

        controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            Position = position,
            MaxSeekTime = duration,
            EndTime = duration
        });

        _lastTimelineUpdate = now;
        _lastTimelinePosition = position;
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                PlayRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.Pause:
                PauseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.Next:
                NextRequested?.Invoke(this, EventArgs.Empty);
                break;
            case SystemMediaTransportControlsButton.Previous:
                PreviousRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnPlaybackPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args)
    {
        SeekRequested?.Invoke(this, ClampPosition(args.RequestedPlaybackPosition, GetDuration()));
    }

    private TimeSpan GetDuration()
    {
        lock (_gate)
        {
            return _duration;
        }
    }

    private static TimeSpan ClampPosition(TimeSpan position, TimeSpan duration)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (duration <= TimeSpan.Zero || position <= duration)
        {
            return position;
        }

        return duration;
    }

    private void DisposePlayer()
    {
        if (_controls is not null)
        {
            _controls.ButtonPressed -= OnButtonPressed;
            _controls.PlaybackPositionChangeRequested -= OnPlaybackPositionChangeRequested;
            _controls = null;
        }

        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
        _initialized = false;
    }
}
#else
#pragma warning disable CS0067
using AvaPlayer.Models;

namespace AvaPlayer.Services.MediaTransport;

public sealed class SmtcMediaTransportService : IMediaTransportService
{
    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler<TimeSpan>? SeekRequested;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateTrackAsync(Track? track, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void UpdatePlaybackState(bool isPlaying)
    {
    }

    public void UpdatePosition(TimeSpan position, TimeSpan duration)
    {
    }

    public void UpdatePlaybackMode(PlaybackMode playbackMode)
    {
    }

    public void Dispose()
    {
    }
}
#pragma warning restore CS0067
#endif
