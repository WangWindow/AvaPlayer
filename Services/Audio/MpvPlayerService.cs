using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace AvaPlayer.Services.Audio;

public sealed class MpvPlayerService : IPlayerService
{
    private const ulong PositionReplyId = 1;
    private const ulong DurationReplyId = 2;
    private const ulong PauseReplyId = 3;

    private readonly CancellationTokenSource _eventLoopCts = new();
    private readonly Thread? _eventLoopThread;
    private IntPtr _handle;
    private double _volume = 80;

    public MpvPlayerService()
    {
        MpvNativeLoader.Configure();

        try
        {
            _handle = MpvInterop.Create();
            if (_handle == IntPtr.Zero)
            {
                InitializationError = "无法创建 libmpv 实例。";
                Console.Error.WriteLine($"[AvaPlayer] {InitializationError}");
                return;
            }

            Check(MpvInterop.SetOptionString(_handle, "terminal", "no"), "terminal");
            Check(MpvInterop.SetOptionString(_handle, "audio-display", "no"), "audio-display");
            Check(MpvInterop.SetOptionString(_handle, "video", "no"), "video");
            Check(MpvInterop.SetOptionString(_handle, "idle", "yes"), "idle");
            Check(MpvInterop.SetOptionString(_handle, "keep-open", "yes"), "keep-open");
            Check(MpvInterop.Initialize(_handle), "initialize");
            Check(MpvInterop.ObserveProperty(_handle, PositionReplyId, "time-pos", MpvFormat.Double), "observe time-pos");
            Check(MpvInterop.ObserveProperty(_handle, DurationReplyId, "duration", MpvFormat.Double), "observe duration");
            Check(MpvInterop.ObserveProperty(_handle, PauseReplyId, "pause", MpvFormat.Flag), "observe pause");
            Check(MpvInterop.SetPropertyString(_handle, "volume", _volume.ToString(CultureInfo.InvariantCulture)), "volume");

            IsReady = true;
            Console.WriteLine("[AvaPlayer] mpv 音频引擎初始化成功");

            _eventLoopThread = new Thread(EventLoop)
            {
                IsBackground = true,
                Name = "mpv-event-loop"
            };
            _eventLoopThread.Start();
        }
        catch (Exception ex)
        {
            InitializationError = ex.Message;
            Console.Error.WriteLine($"[AvaPlayer] mpv 初始化失败: {ex.Message}");

            if (_handle != IntPtr.Zero)
            {
                MpvInterop.TerminateDestroy(_handle);
                _handle = IntPtr.Zero;
            }
        }
    }

    public bool IsReady { get; }

    public string? InitializationError { get; }

    public bool IsPlaying { get; private set; }

    public double Duration { get; private set; }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 100);
            if (_handle != IntPtr.Zero)
            {
                Check(MpvInterop.SetPropertyString(_handle, "volume", _volume.ToString(CultureInfo.InvariantCulture)), "volume");
            }
        }
    }

    public event EventHandler<bool>? PlaybackStateChanged;
    public event EventHandler<double>? PositionChanged;
    public event EventHandler? TrackLoaded;
    public event EventHandler? TrackEnded;

    public Task PlayAsync(string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_handle == IntPtr.Zero)
        {
            Console.Error.WriteLine("[AvaPlayer] libmpv 不可用，无法播放。");
            return Task.CompletedTask;
        }

        ExecuteCommand("loadfile", filePath, "replace");
        Check(MpvInterop.SetPropertyString(_handle, "pause", "no"), "pause=no");
        return Task.CompletedTask;
    }

    public void Pause()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        Check(MpvInterop.SetPropertyString(_handle, "pause", "yes"), "pause=yes");
    }

    public void Resume()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        Check(MpvInterop.SetPropertyString(_handle, "pause", "no"), "pause=no");
    }

    public void Stop()
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        ExecuteCommand("stop");
    }

    public void Seek(double seconds)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        ExecuteCommand("seek", Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture), "absolute");
    }

    public void Dispose()
    {
        _eventLoopCts.Cancel();
        _eventLoopThread?.Join(TimeSpan.FromSeconds(1));

        if (_handle != IntPtr.Zero)
        {
            MpvInterop.TerminateDestroy(_handle);
            _handle = IntPtr.Zero;
        }

        _eventLoopCts.Dispose();
    }

    private void EventLoop()
    {
        while (!_eventLoopCts.IsCancellationRequested && _handle != IntPtr.Zero)
        {
            var eventPointer = MpvInterop.WaitEvent(_handle, 0.05);
            if (eventPointer == IntPtr.Zero)
            {
                continue;
            }

            var mpvEvent = Marshal.PtrToStructure<MpvEvent>(eventPointer);
            switch (mpvEvent.EventId)
            {
                case MpvEventId.FileLoaded:
                    Dispatcher.UIThread.Post(() => TrackLoaded?.Invoke(this, EventArgs.Empty));
                    break;

                case MpvEventId.EndFile:
                    var endFile = Marshal.PtrToStructure<MpvEventEndFile>(mpvEvent.Data);
                    if (endFile.Reason == MpvEndFileReason.Eof)
                    {
                        Dispatcher.UIThread.Post(() => TrackEnded?.Invoke(this, EventArgs.Empty));
                    }

                    break;

                case MpvEventId.PropertyChange:
                    HandlePropertyChange(mpvEvent);
                    break;

                case MpvEventId.Shutdown:
                    return;
            }
        }
    }

    private unsafe void HandlePropertyChange(MpvEvent mpvEvent)
    {
        if (mpvEvent.Data == IntPtr.Zero)
        {
            return;
        }

        var property = Marshal.PtrToStructure<MpvEventProperty>(mpvEvent.Data);
        var propertyName = Marshal.PtrToStringUTF8(property.Name);
        if (string.IsNullOrWhiteSpace(propertyName) || property.Data == IntPtr.Zero)
        {
            return;
        }

        switch (propertyName)
        {
            case "time-pos" when property.Format == MpvFormat.Double:
                var position = *(double*)property.Data;
                Dispatcher.UIThread.Post(() => PositionChanged?.Invoke(this, position));
                break;

            case "duration" when property.Format == MpvFormat.Double:
                Duration = *(double*)property.Data;
                break;

            case "pause" when property.Format == MpvFormat.Flag:
                var paused = *(int*)property.Data != 0;
                IsPlaying = !paused;
                Dispatcher.UIThread.Post(() => PlaybackStateChanged?.Invoke(this, IsPlaying));
                break;
        }
    }

    private unsafe void ExecuteCommand(params string[] arguments)
    {
        var pointers = new IntPtr[arguments.Length + 1];

        try
        {
            for (var i = 0; i < arguments.Length; i++)
            {
                pointers[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
            }

            fixed (IntPtr* pinned = pointers)
            {
                var result = MpvInterop.Command(_handle, (byte**)pinned);
                Check(result, string.Join(' ', arguments));
            }
        }
        finally
        {
            foreach (var pointer in pointers)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
    }

    private static void Check(int errorCode, string operation)
    {
        if (errorCode >= 0)
        {
            return;
        }

        throw new InvalidOperationException($"mpv {operation} 失败: {GetErrorString(errorCode)}");
    }

    private static string GetErrorString(int errorCode)
    {
        var pointer = MpvInterop.ErrorString(errorCode);
        return pointer == IntPtr.Zero
            ? $"error {errorCode}"
            : Marshal.PtrToStringUTF8(pointer) ?? $"error {errorCode}";
    }
}
