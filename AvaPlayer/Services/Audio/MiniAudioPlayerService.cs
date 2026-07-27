using Avalonia.Threading;
using MiniAudioEx.Core.StandardAPI;

namespace AvaPlayer.Services.Audio;

/// <summary>
/// Implements <see cref="IPlayerService"/> using MiniAudioExNET (JAJ.Packages.MiniAudioEx v3.3.5).
/// AOT-safe: no reflection, no unsafe code, no dynamic dispatch.
/// </summary>
public sealed class MiniAudioPlayerService : IPlayerService
{
    private const int TimerIntervalMs = 50;
    private const int DefaultSampleRate = 44100;
    private const int DefaultChannels = 2;

    private readonly object _gate = new();
    private readonly DispatcherTimer _timer;
    private AudioSource? _source;
    private AudioClip? _clip;
    private double _volume = 80;
    private ulong _pausedCursor;
    // MiniAudioEx may apply a delayed cursor reset when Play() starts a source.
    // Keep the requested resume cursor until the first timer pump can confirm
    // that the backend accepted it.
    private ulong _pendingResumeCursor;
    private bool _trackingPlayback;
    private bool _trackEndSignaled;
    private bool _disposed;

    public MiniAudioPlayerService()
    {
        try
        {
            AudioContext.Initialize((uint)DefaultSampleRate, (uint)DefaultChannels);
            IsReady = true;
            Console.WriteLine("[AvaPlayer] MiniAudioEx 音频引擎初始化成功");
        }
        catch (Exception ex)
        {
            InitializationError = $"MiniAudioEx 初始化失败: {ex.Message}";
            Console.Error.WriteLine($"[AvaPlayer] {InitializationError}");
        }

        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(TimerIntervalMs),
            DispatcherPriority.Background,
            OnTimerTick);
        _timer.Start();
    }

    public bool IsReady { get; }

    public string? InitializationError { get; }

    public bool IsPlaying { get; private set; }

    /// <summary>
    /// Current playback position in seconds, or 0 when no track is loaded.
    /// While paused (<see cref="_trackingPlayback"/> is false) returns the
    /// preserved <see cref="_pausedCursor"/> so restored-session and
    /// seek-while-paused positions are visible to consumers (MPRIS, PlayerBar).
    /// While playing returns the live <see cref="AudioSource.Cursor"/>.
    /// </summary>
    public double Position
    {
        get
        {
            lock (_gate)
            {
                if (_source is null)
                    return 0;

                var sampleRate = AudioContext.SampleRate;
                if (sampleRate <= 0)
                    return 0;

                return _trackingPlayback
                    ? (double)_source.Cursor / sampleRate
                    : (double)_pausedCursor / sampleRate;
            }
        }
    }

    /// <summary>
    /// Duration in seconds. Returns 0 when no track is loaded.
    /// Computed from <see cref="AudioSource.Length"/> / <see cref="AudioContext.SampleRate"/>.
    /// </summary>
    public double Duration
    {
        get
        {
            lock (_gate)
            {
                if (_source is null)
                    return 0;

                var sampleRate = AudioContext.SampleRate;
                return sampleRate > 0
                    ? (double)_source.Length / sampleRate
                    : 0;
            }
        }
    }

    /// <summary>
    /// Volume in 0-100 range (ViewModel convention).
    /// Maps to <see cref="AudioSource.Volume"/> 0.0-1.0 range internally.
    /// </summary>
    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);

            lock (_gate)
            {
                if (_source is not null)
                {
                    _source.Volume = (float)(_volume / 100.0);
                }
            }
        }
    }

    public event EventHandler<bool>? PlaybackStateChanged;
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? TrackLoaded;
    public event EventHandler? TrackEnded;

    public Task PlayAsync(
        string filePath,
        bool startPaused = false,
        double startPositionSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsReady)
        {
            throw new InvalidOperationException(
                $"MiniAudioEx 音频引擎不可用，无法播放: {filePath}. " +
                (InitializationError ?? "未提供初始化错误信息。"));
        }

        bool wasPlaying;
        Exception? loadError = null;

        lock (_gate)
        {
            wasPlaying = IsPlaying;
            DisposeCurrentInternal();

            AudioClip? clip = null;
            AudioSource? source = null;

            try
            {
                clip = new AudioClip(filePath);
                source = new AudioSource();

                _clip = clip;
                _source = source;
                _trackEndSignaled = false;
                _pausedCursor = 0;
                _pendingResumeCursor = 0;

                // Apply current volume to the new source
                source.Volume = (float)(_volume / 100.0);

                // Start playback from the beginning
                source.Play(clip);
                source.End += OnSourceEnded;

                // Seek to requested position if specified
                ulong requestedFrame = 0;
                if (startPositionSeconds > 0)
                {
                    var sampleRate = AudioContext.SampleRate;
                    if (sampleRate > 0)
                    {
                        requestedFrame = (ulong)(startPositionSeconds * sampleRate);
                        var length = source.Length;
                        requestedFrame = Math.Min(requestedFrame, length > 0 ? length - 1UL : 0UL);
                        source.Cursor = requestedFrame;
                    }
                }

                // Pause immediately if requested (seek applied first, so position is correct)
                if (startPaused)
                {
                    // Do not read source.Cursor back here. MiniAudioEx can
                    // still report zero until its first AudioContext.Update,
                    // even though the requested cursor has already been set.
                    // Preserve the requested frame as the authoritative
                    // paused position instead.
                    _pausedCursor = requestedFrame;
                    source.Stop();
                    _trackingPlayback = false;
                    IsPlaying = false;
                }
                else
                {
                    _trackingPlayback = true;
                    IsPlaying = true;
                }
            }
            catch (Exception ex)
            {
                source?.Dispose();
                clip?.Dispose();
                _source = null;
                _clip = null;
                _trackingPlayback = false;
                IsPlaying = false;
                _pausedCursor = 0;

                loadError = new InvalidOperationException($"加载音频文件失败: {filePath}", ex);
            }
        }

        if (loadError is not null)
        {
            if (wasPlaying)
            {
                PlaybackStateChanged?.Invoke(this, false);
            }

            throw loadError;
        }

        // Fire events outside lock to avoid nested lock risk from subscribers
        PlaybackStateChanged?.Invoke(this, IsPlaying);
        TrackLoaded?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pauses playback by stopping the source and saving the cursor position.
    /// MiniAudioExNET AudioSource has no dedicated Pause method;
    /// we emulate it by Stop + cursor save, restored on Resume.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_source is null || _clip is null)
                return;

            _pausedCursor = _source.Cursor;
            _source.Stop();
            _trackingPlayback = false;
            IsPlaying = false;
        }

        PlaybackStateChanged?.Invoke(this, false);
    }

    /// <summary>
    /// Resumes playback from the saved cursor position.
    /// </summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_source is null || _clip is null)
                return;

            _source.Play(_clip);
            var resumeCursor = _pausedCursor;
            _source.Cursor = resumeCursor;
            _pendingResumeCursor = resumeCursor;
            _pausedCursor = 0;
            _trackingPlayback = true;
            IsPlaying = true;
        }

        PlaybackStateChanged?.Invoke(this, true);
    }

    /// <summary>
    /// Stops playback and releases all audio resources for the current track.
    /// </summary>
    public void Stop()
    {
        bool hadResources;

        lock (_gate)
        {
            hadResources = _source is not null || _clip is not null;

            _source?.Stop();
            _trackingPlayback = false;
            IsPlaying = false;
            _pausedCursor = 0;
            _pendingResumeCursor = 0;
            DisposeCurrentInternal();
        }

        if (hadResources)
        {
            PlaybackStateChanged?.Invoke(this, false);
        }
    }

    /// <summary>
    /// Seeks to the specified position in seconds.
    /// Uses <see cref="AudioSource.Cursor"/> to set the PCM frame position.
    /// While paused, also mirrors the new position into <see cref="_pausedCursor"/>
    /// so that the next <see cref="Resume"/> reads the up-to-date value instead of
    /// the position captured at <see cref="Pause"/> time.
    /// </summary>
    public void Seek(double seconds)
    {
        lock (_gate)
        {
            if (_source is null)
                return;

            var sampleRate = AudioContext.SampleRate;
            if (sampleRate <= 0)
                return;

            var clampedSeconds = Math.Max(0.0, seconds);
            var frame = (ulong)(clampedSeconds * sampleRate);
            var length = _source.Length;
            frame = Math.Min(frame, length > 0 ? length - 1UL : 0UL);

            _source.Cursor = frame;

            // Keep _pausedCursor in sync when paused: Resume() reads from
            // _pausedCursor, so without this mirror a seek-while-paused would
            // be silently discarded when the user resumes playback.
            if (!_trackingPlayback)
            {
                _pausedCursor = frame;
            }
            else
            {
                _pendingResumeCursor = 0;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();

        lock (_gate)
        {
            _trackingPlayback = false;
            IsPlaying = false;
            _pausedCursor = 0;
            _pendingResumeCursor = 0;
            DisposeCurrentInternal();
        }

        if (IsReady)
        {
            try
            {
                AudioContext.Deinitialize();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[AvaPlayer] MiniAudioEx 反初始化失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Releases the current <see cref="AudioSource"/> and <see cref="AudioClip"/>.
    /// Caller must hold <see cref="_gate"/> lock.
    /// </summary>
    private void DisposeCurrentInternal()
    {
        if (_source is not null)
        {
            _source.End -= OnSourceEnded;
            _source.Dispose();
            _source = null;
        }

        if (_clip is not null)
        {
            _clip.Dispose();
            _clip = null;
        }
    }

    private void OnSourceEnded()
    {
        AudioSource? source;
        lock (_gate)
        {
            source = _source;
        }

        if (source is not null)
        {
            SignalTrackEnded(source);
        }
    }

    private void SignalTrackEnded(AudioSource source)
    {
        bool shouldFireEnded;

        lock (_gate)
        {
            if (_disposed || !_trackingPlayback || !ReferenceEquals(_source, source))
            {
                return;
            }

            if (source.IsPlaying)
            {
                try { source.Stop(); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[AvaPlayer] 曲终停止 source 失败: {ex.Message}");
                }
            }

            _trackingPlayback = false;
            IsPlaying = false;

            if (!_trackEndSignaled)
            {
                _trackEndSignaled = true;
                shouldFireEnded = true;
            }
            else
            {
                shouldFireEnded = false;
            }
        }

        PlaybackStateChanged?.Invoke(this, false);

        if (shouldFireEnded)
        {
            Console.WriteLine("[AvaPlayer] 检测到曲目播放结束");
            TrackEnded?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Timer callback running on UI thread (~50ms interval).
    /// Performs three tasks:
    ///   1. Pumps <see cref="AudioContext.Update()"/> (required by MiniAudioEx)
    ///   2. Polls playback position and fires <see cref="PositionChanged"/>
    ///   3. Detects natural track end and fires <see cref="TrackEnded"/>
    /// </summary>
    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;

        // Step 1: Pump the audio engine (mandatory, must be on main thread)
        try
        {
            AudioContext.Update();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AvaPlayer] AudioContext.Update 异常: {ex.Message}");
        }

        AudioSource? source;
        AudioClip? clip;
        bool trackingPlayback;
        ulong pausedCursor;

        lock (_gate)
        {
            source = _source;
            clip = _clip;
            trackingPlayback = _trackingPlayback;
            pausedCursor = _pausedCursor;
        }

        if (source is null || clip is null)
            return;

        // Step 2: Poll current position and fire PositionChanged
        // IMPORTANT: When paused, use _pausedCursor instead of source.Cursor
        // because source.Stop() resets source.Cursor to 0. Without this fix,
        // the PositionChanged event fires 0 for paused/restored tracks, causing
        // MPRIS and PlayerBar to display 0 despite the correct snapshot position.
        double position;
        bool reachedEnd;
        try
        {
            var cursor = trackingPlayback ? source.Cursor : pausedCursor;
            ulong pendingResumeCursor;
            lock (_gate)
            {
                pendingResumeCursor = _pendingResumeCursor;
                if (trackingPlayback && pendingResumeCursor > 0)
                {
                    if (cursor < pendingResumeCursor)
                    {
                        source.Cursor = pendingResumeCursor;
                        cursor = pendingResumeCursor;
                    }

                    _pendingResumeCursor = 0;
                }
            }
            var length = source.Length;
            var sampleRate = AudioContext.SampleRate;
            position = sampleRate > 0 ? (double)cursor / sampleRate : 0.0;

            // Source of truth for "playback finished": cursor reached the end.
            // MiniAudioEx's IsPlaying flag is not always cleared on natural
            // completion, so we must not rely on it alone for state transitions.
            // Only check end-of-track when actively tracking playback.
            reachedEnd = trackingPlayback && length > 0 && cursor >= length;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AvaPlayer] 获取播放位置失败: {ex.Message}");
            return;
        }

        // Step 3: Detect natural track end and reconcile state machine
        // Trigger condition is either:
        //   (a) source.IsPlaying has gone false (engine notified us), or
        //   (b) cursor reached the end (definitive: position >= duration)
        // In case (b) we must explicitly Stop the source so the engine state
        // matches our local state; otherwise the engine keeps reporting
        // IsPlaying=true and the state machine drifts out of sync.
        if (trackingPlayback && (!source.IsPlaying || reachedEnd))
        {
            SignalTrackEnded(source);
        }

        // Step 4: Publish the (possibly clamped) position to listeners
        PositionChanged?.Invoke(this, position);
    }
}
