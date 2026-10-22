using System.Globalization;
using System.Threading.Channels;
using AvaPlayer.Models;
using AvaPlayer.Helpers;
using AvaPlayer.Services.Audio;
using AvaPlayer.Services.Playlist;
using AvaPlayer.Services.Settings;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.PlaybackSession;

/// <summary>
/// Authoritative playback session. Serialized command execution,
/// immutable snapshots, subscriber fan-out, and eventual persistence ownership.
/// </summary>
public sealed class PlaybackSession : IPlaybackSessionClient, IDisposable
{
    private readonly IPlayerService _player;
    private readonly IPlaylistService _playlist;
    private readonly ISettingsService _settings;
    private readonly IPlaybackPositionMemoryService _positionMemory;
    private readonly ILogger<PlaybackSession> _logger;
    private readonly Channel<Func<CancellationToken, Task>> _commands;
    private readonly List<Action<PlaybackSnapshot>> _subscribers = new();
    private readonly object _subscriberGate = new();

    private long _revision;
    private bool _disposed;
    private bool _isStartingTrack;
    private readonly CancellationTokenSource _loopCts = new();
    private readonly CancellationTokenSource _checkpointCts = new();

    private const int CheckpointIntervalSeconds = 30;

    // ── Ctor ──

    public PlaybackSession(
        IPlayerService player,
        IPlaylistService playlist,
        ISettingsService settings,
        IPlaybackPositionMemoryService positionMemory,
        ILogger<PlaybackSession> logger)
    {
        _player = player;
        _playlist = playlist;
        _settings = settings;
        _positionMemory = positionMemory;
        _logger = logger;
        _commands = Channel.CreateUnbounded<Func<CancellationToken, Task>>(
            new UnboundedChannelOptions { SingleReader = true });

        _player.PlaybackStateChanged += OnPlayerPlaybackStateChanged;
        _player.PositionChanged += OnPlayerPositionChanged;
        _player.TrackLoaded += OnPlayerTrackLoaded;
        _player.TrackEnded += OnPlayerTrackEnded;

        _ = RunCommandLoopAsync(_loopCts.Token);
        _ = RunCheckpointLoopAsync(_checkpointCts.Token);
    }

    // ── Public state ──

    public PlaybackSnapshot LatestSnapshot { get; private set; } = PlaybackSnapshot.Idle;

    public Task RestorePlaybackAtPositionAsync(double positionSeconds, CancellationToken ct = default) =>
        EnqueueAsync(ct, ct2 => DoRestorePlaybackAtPositionAsync(Math.Max(0, positionSeconds), ct2));

    public async Task<double> GetSavedPositionAsync(CancellationToken ct = default)
    {
        if (!_positionMemory.IsEnabled) return 0;
        try
        {
            var saved = await _settings.GetAsync("playback-position-seconds", ct);
            if (double.TryParse(saved, NumberStyles.Float, CultureInfo.InvariantCulture, out var pos))
                return Math.Max(0, pos);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _logger.LogError(ex, "[PlaybackSession] Read position failed"); }
        return 0;
    }

    public async Task RestoreTrackAsync(CancellationToken ct = default)
    {
        var savedPath = await _settings.GetAsync("current-track-path", ct);
        if (string.IsNullOrWhiteSpace(savedPath))
        {
            if (_playlist.Queue.Count > 0 && _playlist.CurrentTrack is null)
                _playlist.SetCurrentTrack(_playlist.Queue[0]);
            return;
        }

        var playbackPlaylistId = await _settings.GetAsync("current-track-playlist-id", ct);
        if (!string.IsNullOrEmpty(playbackPlaylistId)
            && playbackPlaylistId != _playlist.SelectedPlaylist?.Id
            && _playlist.Playlists.Any(p => p.Id == playbackPlaylistId))
            await _playlist.SelectPlaylistAsync(playbackPlaylistId, ct);

        var matched = _playlist.Queue.FirstOrDefault(t =>
            FilePaths.Comparer.Equals(t.FilePath, savedPath));

        if (matched is not null)
            _playlist.SetCurrentTrack(matched);
        else if (_playlist.Queue.Count > 0)
            _playlist.SetCurrentTrack(_playlist.Queue[0]);
    }

    /// <summary>
    /// Persists the current track path and (when position memory is enabled)
    /// the last-known playback position atomically to settings storage.
    /// Never queries IPlayerService.Position directly during teardown.
    /// Uses a single batch write to ensure atomicity of the pair.
    /// </summary>
    public async Task PersistAsync(CancellationToken ct = default)
    {
        var snapshot = LatestSnapshot;
        if (snapshot.CurrentTrack is not { } track) return;

        var settings = new Dictionary<string, string>
        {
            ["current-track-path"] = track.FilePath
        };

        if (!string.IsNullOrEmpty(track.PlaylistId))
            settings["current-track-playlist-id"] = track.PlaylistId;

        if (_positionMemory.IsEnabled)
        {
            var pos = Math.Max(0, snapshot.Position)
                .ToString("F6", CultureInfo.InvariantCulture);
            settings["playback-position-seconds"] = pos;
        }

        // Always persist volume alongside track/position
        var vol = Math.Clamp(snapshot.Volume, 0, 100)
            .ToString(CultureInfo.InvariantCulture);
        settings["player-volume"] = vol;

        await _settings.SaveSettingsBatchAsync(settings, ct);
    }

    // ── Subscribe ──

    public IDisposable Subscribe(Action<PlaybackSnapshot> onSnapshot)
    {
        lock (_subscriberGate)
        {
            _subscribers.Add(onSnapshot);
            try { onSnapshot(LatestSnapshot); }
            catch
            {
                _subscribers.Remove(onSnapshot);
                throw;
            }
        }
        return new Unsubscriber(this, onSnapshot);
    }

    private void Unsubscribe(Action<PlaybackSnapshot> onSnapshot)
    {
        lock (_subscriberGate) _subscribers.Remove(onSnapshot);
    }

    // ── Commands ──

    public Task TogglePlayPauseAsync(CancellationToken ct = default) =>
        EnqueueAsync(ct, ct2 => _player.IsPlaying ? DoPauseAsync(ct2) : DoResumeAsync(ct2));

    public Task PauseAsync(CancellationToken ct = default) =>
        EnqueueAsync(ct, DoPauseAsync);

    public Task ResumeAsync(CancellationToken ct = default) =>
        EnqueueAsync(ct, DoResumeAsync);

    public Task NextAsync(CancellationToken ct = default) =>
        EnqueueAsync(ct, DoNextAsync);

    public Task PreviousAsync(CancellationToken ct = default) =>
        EnqueueAsync(ct, DoPreviousAsync);

    public Task SeekAsync(double seconds, CancellationToken ct = default) =>
        EnqueueAsync(ct, ct2 => DoSeekAsync(seconds, ct2));

    public Task SetVolumeAsync(double volume, CancellationToken ct = default) =>
        EnqueueAsync(ct, _ => DoSetVolume(volume));

    public Task CyclePlaybackModeAsync(CancellationToken ct = default) =>
        EnqueueAsync(ct, _ => DoCyclePlaybackMode());

    public Task<PlaybackStartResult> PlayTrackAsync(Track track, CancellationToken ct = default) =>
        EnqueueWithResultAsync(ct, ct2 => DoPlayTrackAsync(track, ct2));

    // ── Command loop ──

    private async Task RunCommandLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cmd = await _commands.Reader.ReadAsync(ct);
                await cmd(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PlaybackSession] Command failed");
            }
        }
    }

    private Task EnqueueAsync(CancellationToken ct, Func<CancellationToken, Task> command) =>
        EnqueueWithResultAsync(ct, async token =>
        {
            await command(token);
            return true;
        });

    private async Task<T> EnqueueWithResultAsync<T>(CancellationToken ct, Func<CancellationToken, Task<T>> command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _loopCts.Token);
        var token = linked.Token;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(async _ =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                completion.TrySetResult(await command(token));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                completion.TrySetCanceled(token);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }, token);
        return await completion.Task.WaitAsync(token);
    }

    // ── Command implementations ──

    private Task DoPauseAsync(CancellationToken ct)
    {
        _player.Pause();
        Publish(ps => ps with { Status = PlaybackStatus.Paused, Position = _player.Position });
        return Task.CompletedTask;
    }

    private async Task DoResumeAsync(CancellationToken ct)
    {
        if (LatestSnapshot.HasTrack && LatestSnapshot.Status != PlaybackStatus.Stopped)
        {
            _player.Resume();
            Publish(ps => ps with { Status = PlaybackStatus.Playing });
        }
        else if ((LatestSnapshot.CurrentTrack ?? _playlist.CurrentTrack) is { } track)
        {
            await DoPlayTrackAsync(track, ct);
        }
    }

    private Task DoNextAsync(CancellationToken ct) => AdvanceAsync(_playlist.GetNextTrack(), false, ct);

    private Task DoPreviousAsync(CancellationToken ct) => AdvanceAsync(_playlist.GetPreviousTrack(), false, ct);

    private async Task AdvanceAsync(Track? track, bool stopAtEnd, CancellationToken ct)
    {
        if (track is not null)
            await DoPlayTrackAsync(track, ct);
        else if (stopAtEnd)
            Publish(ps => ps with { Status = PlaybackStatus.Stopped });
    }

    private Task DoSeekAsync(double seconds, CancellationToken ct)
    {
        if (!double.IsFinite(seconds)) return Task.CompletedTask;
        var position = Math.Max(0, seconds);
        _player.Seek(position);
        Publish(ps => ps with { Position = position });
        return Task.CompletedTask;
    }

    private Task DoSetVolume(double volume)
    {
        var clamped = Math.Clamp(volume, 0, 100);
        if (_player.IsReady) _player.Volume = clamped;
        Publish(ps => ps with { Volume = clamped });
        return Task.CompletedTask;
    }

    private Task DoCyclePlaybackMode()
    {
        var next = _playlist.PlaybackMode switch
        {
            PlaybackMode.Sequential => PlaybackMode.RepeatAll,
            PlaybackMode.RepeatAll => PlaybackMode.RepeatOne,
            PlaybackMode.RepeatOne => PlaybackMode.Shuffle,
            PlaybackMode.Shuffle => PlaybackMode.Sequential,
            _ => PlaybackMode.Sequential
        };
        _playlist.PlaybackMode = next;
        Publish(ps => ps with { PlaybackMode = next });
        return Task.CompletedTask;
    }

    private async Task DoRestorePlaybackAtPositionAsync(double positionSeconds, CancellationToken ct)
    {
        Track? track;
        if (LatestSnapshot.HasTrack)
        {
            track = LatestSnapshot.CurrentTrack;
            _player.Seek(positionSeconds);
            _player.Pause();
        }
        else if ((track = _playlist.CurrentTrack) is not null)
        {
            _playlist.SetCurrentTrack(track);
            try
            {
                await _player.PlayAsync(
                    track.FilePath,
                    startPaused: true,
                    startPositionSeconds: positionSeconds,
                    cancellationToken: ct);

                // Some audio backends apply the initial cursor while starting
                // and then reset it as part of Stop(). Re-apply the restore
                // position after the source has been fully loaded so the
                // paused cursor used by Resume() is authoritative.
                _player.Seek(positionSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PlaybackSession] Restore playback failed: {Message}", ex.Message);
                return;
            }
        }
        else
        {
            return;
        }

        Publish(ps => ps with
        {
            CurrentTrack = track,
            Status = PlaybackStatus.Paused,
            Position = positionSeconds,
            Duration = track is not null ? Math.Max(track.DurationSeconds, 1) : ps.Duration
        });
    }

    private async Task<PlaybackStartResult> DoPlayTrackAsync(Track track, CancellationToken ct)
    {
        var previous = LatestSnapshot;
        _isStartingTrack = true;
        try
        {
            await _player.PlayAsync(track.FilePath, cancellationToken: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PlaybackSession] Cannot start {Path}", track.FilePath);
            if (previous.HasTrack)
                Publish(ps => ps with
                {
                    CurrentTrack = previous.CurrentTrack,
                    Status = PlaybackStatus.Stopped,
                    Position = previous.Position
                });
            var kind = ex switch
            {
                FileNotFoundException => PlaybackStartFailureKind.FileNotFound,
                _ when !_player.IsReady => PlaybackStartFailureKind.EngineUnavailable,
                _ => PlaybackStartFailureKind.LoadFailed
            };
            return new PlaybackStartResult.Failed(kind, ex.Message);
        }
        finally { _isStartingTrack = false; }

        _playlist.SetCurrentTrack(track);
        Publish(ps => ps with
        {
            CurrentTrack = track,
            Status = PlaybackStatus.Playing,
            Duration = Math.Max(_player.Duration > 1 ? _player.Duration : track.DurationSeconds, 1),
            Position = 0,
            PlaybackMode = _playlist.PlaybackMode
        });
        return new PlaybackStartResult.Started();
    }

    // ── Event handlers ──

    private void OnPlayerPlaybackStateChanged(object? sender, bool isPlaying)
    {
        if (_isStartingTrack) return;
        var status = isPlaying ? PlaybackStatus.Playing : PlaybackStatus.Paused;
        Publish(ps => ps with { Status = status, Position = _player.Position });
    }

    private void OnPlayerTrackLoaded(object? sender, EventArgs e)
    {
        if (_isStartingTrack) return;
        Publish(ps => ps with
        {
            Duration = Math.Max(_player.Duration, 1),
            Position = _player.Position
        });
    }

    private void OnPlayerPositionChanged(object? sender, double position)
    {
        if (_isStartingTrack) return;
        Publish(ps => ps with { Position = position });
    }

    private void OnPlayerTrackEnded(object? sender, EventArgs e)
    {
        if (_isStartingTrack) return;
        // Enqueue auto-advance through the serialized command loop.
        // The command loop single-reader ensures this runs after any in-flight
        // user commands, avoiding races between manual Next/Previous and
        // automatic track-end transitions.
        var endedTrack = LatestSnapshot.CurrentTrack;
        _commands.Writer.TryWrite(ct => ReferenceEquals(endedTrack, LatestSnapshot.CurrentTrack)
            ? DoAutoAdvanceAsync(ct)
            : Task.CompletedTask);
    }

    private Task DoAutoAdvanceAsync(CancellationToken ct) => AdvanceAsync(_playlist.GetNextTrack(), true, ct);

    // ── Checkpoint ──

    /// <summary>
    /// Periodic checkpoint loop that persists session state every 30 seconds
    /// while a valid track is loaded. Runs independently of the command loop
    /// so explicit PersistAsync is never blocked by queued playback commands.
    /// Cancelled on Dispose.
    /// </summary>
    private async Task RunCheckpointLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(CheckpointIntervalSeconds), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }

            if (ct.IsCancellationRequested || !LatestSnapshot.HasTrack)
                continue;

            try
            {
                await PersistAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PlaybackSession] Checkpoint persist failed");
            }
        }
    }

    // ── Snapshot publishing ──

    private void Publish(Func<PlaybackSnapshot, PlaybackSnapshot> transform)
    {
        lock (_subscriberGate)
        {
            var snap = transform(LatestSnapshot) with { Revision = ++_revision };
            LatestSnapshot = snap;
            foreach (var subscriber in _subscribers.ToArray())
            {
                try { subscriber(snap); }
                catch (Exception ex) { _logger.LogWarning(ex, "[PlaybackSession] Subscriber failed"); }
            }
        }
    }

    // ── Disposal ──

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _commands.Writer.TryComplete();
        _checkpointCts.Cancel();
        _loopCts.Cancel();
        _player.PlaybackStateChanged -= OnPlayerPlaybackStateChanged;
        _player.PositionChanged -= OnPlayerPositionChanged;
        _player.TrackLoaded -= OnPlayerTrackLoaded;
        _player.TrackEnded -= OnPlayerTrackEnded;
        _checkpointCts.Dispose();
        _loopCts.Dispose();
    }

    private sealed class Unsubscriber(PlaybackSession owner, Action<PlaybackSnapshot> callback) : IDisposable
    {
        public void Dispose() => owner.Unsubscribe(callback);
    }
}
