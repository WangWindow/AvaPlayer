#if LINUX_MPRIS
using System.Text;
using AvaPlayer.Models;
using Tmds.DBus.Protocol;

namespace AvaPlayer.Services.MediaTransport;

public sealed class MprisMediaTransportService : IMediaTransportService
{
    private const string ServiceName = "org.mpris.MediaPlayer2.avaplayer";
    private const string ObjectPathString = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private static readonly ReadOnlyMemory<byte>[] IntrospectionXml =
    [
        Encoding.UTF8.GetBytes("""
            <interface name="org.mpris.MediaPlayer2">
              <method name="Raise" />
              <method name="Quit" />
              <property name="CanQuit" type="b" access="read" />
              <property name="CanRaise" type="b" access="read" />
              <property name="HasTrackList" type="b" access="read" />
              <property name="Identity" type="s" access="read" />
              <property name="DesktopEntry" type="s" access="read" />
              <property name="SupportedUriSchemes" type="as" access="read" />
              <property name="SupportedMimeTypes" type="as" access="read" />
            </interface>
            """),
        Encoding.UTF8.GetBytes("""
            <interface name="org.mpris.MediaPlayer2.Player">
              <method name="Next" />
              <method name="Previous" />
              <method name="Pause" />
              <method name="PlayPause" />
              <method name="Stop" />
              <method name="Play" />
              <method name="Seek">
                <arg direction="in" name="Offset" type="x" />
              </method>
              <method name="SetPosition">
                <arg direction="in" name="TrackId" type="o" />
                <arg direction="in" name="Position" type="x" />
              </method>
              <property name="PlaybackStatus" type="s" access="read" />
              <property name="LoopStatus" type="s" access="read" />
              <property name="Rate" type="d" access="read" />
              <property name="Shuffle" type="b" access="read" />
              <property name="Metadata" type="a{sv}" access="read" />
              <property name="Volume" type="d" access="read" />
              <property name="Position" type="x" access="read" />
              <property name="MinimumRate" type="d" access="read" />
              <property name="MaximumRate" type="d" access="read" />
              <property name="CanGoNext" type="b" access="read" />
              <property name="CanGoPrevious" type="b" access="read" />
              <property name="CanPlay" type="b" access="read" />
              <property name="CanPause" type="b" access="read" />
              <property name="CanSeek" type="b" access="read" />
              <property name="CanControl" type="b" access="read" />
            </interface>
            """)
    ];

    private readonly object _gate = new();
    private DBusConnection? _connection;
    private Track? _currentTrack;
    private bool _isPlaying;
    private TimeSpan _position;
    private TimeSpan _duration;
    private PlaybackMode _playbackMode = PlaybackMode.Sequential;
    private bool _initialized;

    public event EventHandler? PlayRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? PreviousRequested;
    public event EventHandler<TimeSpan>? SeekRequested;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized || !OperatingSystem.IsLinux())
        {
            return;
        }

        var address = DBusAddress.Session;
        if (string.IsNullOrWhiteSpace(address))
        {
            Console.Error.WriteLine("[MPRIS] 未找到 session bus 地址，跳过初始化。");
            return;
        }

        try
        {
            _connection = new DBusConnection(address);
            await _connection.ConnectAsync().ConfigureAwait(false);
            _connection.AddMethodHandler(new MprisObject(this));
            await _connection.RequestNameAsync(ServiceName, RequestNameOptions.Default).ConfigureAwait(false);
            _initialized = true;
            Console.WriteLine("[MPRIS] 服务已注册");
        }
        catch (Exception ex)
        {
            _connection?.Dispose();
            _connection = null;
            Console.Error.WriteLine($"[MPRIS] 初始化失败: {ex.Message}");
        }
    }

    public Task UpdateTrackAsync(Track? track, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _currentTrack = track;
            _position = TimeSpan.Zero;
            _duration = track is null
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(Math.Max(0, track.DurationSeconds));
        }

        EmitPropertiesChanged(PlayerInterface, new Dictionary<string, VariantValue>
        {
            ["Metadata"] = GetMetadataVariant(),
            ["CanPlay"] = track is not null,
            ["CanPause"] = track is not null,
            ["CanSeek"] = track is not null
        });

        return Task.CompletedTask;
    }

    public void UpdatePlaybackState(bool isPlaying)
    {
        lock (_gate)
        {
            _isPlaying = isPlaying;
        }

        EmitPropertiesChanged(PlayerInterface, new Dictionary<string, VariantValue>
        {
            ["PlaybackStatus"] = GetPlaybackStatus()
        });
    }

    public void UpdatePosition(TimeSpan position, TimeSpan duration)
    {
        lock (_gate)
        {
            _position = position;
            _duration = duration;
        }
    }

    public void UpdatePlaybackMode(PlaybackMode playbackMode)
    {
        lock (_gate)
        {
            _playbackMode = playbackMode;
        }

        EmitPropertiesChanged(PlayerInterface, new Dictionary<string, VariantValue>
        {
            ["LoopStatus"] = GetLoopStatus(),
            ["Shuffle"] = GetShuffle()
        });
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }

    private void RaisePlayRequested() => PlayRequested?.Invoke(this, EventArgs.Empty);

    private void RaisePauseRequested() => PauseRequested?.Invoke(this, EventArgs.Empty);

    private void RaiseNextRequested() => NextRequested?.Invoke(this, EventArgs.Empty);

    private void RaisePreviousRequested() => PreviousRequested?.Invoke(this, EventArgs.Empty);

    private void RaiseSeekRequested(TimeSpan position) => SeekRequested?.Invoke(this, ClampPosition(position));

    private string GetPlaybackStatus()
    {
        lock (_gate)
        {
            if (_currentTrack is null)
            {
                return "Stopped";
            }

            return _isPlaying ? "Playing" : "Paused";
        }
    }

    private string GetLoopStatus()
    {
        lock (_gate)
        {
            return _playbackMode switch
            {
                PlaybackMode.RepeatAll => "Playlist",
                PlaybackMode.RepeatOne => "Track",
                _ => "None"
            };
        }
    }

    private bool GetShuffle()
    {
        lock (_gate)
        {
            return _playbackMode == PlaybackMode.Shuffle;
        }
    }

    private VariantValue GetMetadataVariant()
    {
        lock (_gate)
        {
            if (_currentTrack is null)
            {
                return new Dict<string, VariantValue>();
            }

            var metadata = new Dictionary<string, VariantValue>
            {
                ["mpris:trackid"] = new ObjectPath($"{ObjectPathString}/track/{SanitizeObjectPathSegment(_currentTrack.Id)}"),
                ["mpris:length"] = ToMicroseconds(TimeSpan.FromSeconds(_currentTrack.DurationSeconds)),
                ["xesam:title"] = _currentTrack.DisplayTitle,
                ["xesam:album"] = _currentTrack.DisplayAlbum,
                ["xesam:url"] = new Uri(_currentTrack.FilePath).AbsoluteUri,
                ["xesam:artist"] = new Array<string>([_currentTrack.DisplayArtist])
            };

            return new Dict<string, VariantValue>(metadata);
        }
    }

    private VariantValue? GetProperty(string interfaceName, string propertyName)
    {
        lock (_gate)
        {
            return interfaceName switch
            {
                RootInterface => propertyName switch
                {
                    "CanQuit" => false,
                    "CanRaise" => false,
                    "HasTrackList" => false,
                    "Identity" => "AvaPlayer",
                    "DesktopEntry" => "AvaPlayer",
                    "SupportedUriSchemes" => new Array<string>(["file"]),
                    "SupportedMimeTypes" => new Array<string>([
                        "audio/mpeg",
                        "audio/flac",
                        "audio/ogg",
                        "audio/mp4",
                        "audio/x-wav"
                    ]),
                    _ => (VariantValue?)null
                },
                PlayerInterface => propertyName switch
                {
                    "PlaybackStatus" => GetPlaybackStatus(),
                    "LoopStatus" => GetLoopStatus(),
                    "Rate" => 1d,
                    "Shuffle" => GetShuffle(),
                    "Metadata" => GetMetadataVariant(),
                    "Volume" => 0.8d,
                    "Position" => ToMicroseconds(_position),
                    "MinimumRate" => 1d,
                    "MaximumRate" => 1d,
                    "CanGoNext" => true,
                    "CanGoPrevious" => true,
                    "CanPlay" => _currentTrack is not null,
                    "CanPause" => _currentTrack is not null,
                    "CanSeek" => _currentTrack is not null,
                    "CanControl" => true,
                    _ => (VariantValue?)null
                },
                _ => (VariantValue?)null
            };
        }
    }

    private Dictionary<string, VariantValue> GetAllProperties(string interfaceName)
    {
        var properties = new Dictionary<string, VariantValue>();

        string[] propertyNames = interfaceName switch
        {
            RootInterface =>
            [
                "CanQuit",
                "CanRaise",
                "HasTrackList",
                "Identity",
                "DesktopEntry",
                "SupportedUriSchemes",
                "SupportedMimeTypes"
            ],
            PlayerInterface =>
            [
                "PlaybackStatus",
                "LoopStatus",
                "Rate",
                "Shuffle",
                "Metadata",
                "Volume",
                "Position",
                "MinimumRate",
                "MaximumRate",
                "CanGoNext",
                "CanGoPrevious",
                "CanPlay",
                "CanPause",
                "CanSeek",
                "CanControl"
            ],
            _ => []
        };

        foreach (var propertyName in propertyNames)
        {
            var value = GetProperty(interfaceName, propertyName);
            if (value is not null)
            {
                properties[propertyName] = value.Value;
            }
        }

        return properties;
    }

    private void EmitPropertiesChanged(string interfaceName, Dictionary<string, VariantValue> changedProperties)
    {
        if (!_initialized || _connection is null || changedProperties.Count == 0)
        {
            return;
        }

        try
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteSignalHeader(null, ObjectPathString, PropertiesInterface, "PropertiesChanged", "sa{sv}as");
            writer.WriteString(interfaceName);
            writer.WriteDictionary(changedProperties);
            writer.WriteArray(Array.Empty<string>());
            _connection.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            _initialized = false;
            Console.Error.WriteLine($"[MPRIS] 发送属性变更信号失败 (连接可能已断开): {ex.Message}");
        }
    }

    private static long ToMicroseconds(TimeSpan value) => value.Ticks / 10;

    private static TimeSpan FromMicroseconds(long value) => TimeSpan.FromTicks(value * 10);

    private TimeSpan ClampPosition(TimeSpan position)
    {
        lock (_gate)
        {
            if (_duration <= TimeSpan.Zero)
            {
                return position < TimeSpan.Zero ? TimeSpan.Zero : position;
            }

            if (position < TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            return position > _duration ? _duration : position;
        }
    }

    private string? GetCurrentTrackObjectPath()
    {
        lock (_gate)
        {
            return _currentTrack is null
                ? null
                : $"{ObjectPathString}/track/{SanitizeObjectPathSegment(_currentTrack.Id)}";
        }
    }

    private static string SanitizeObjectPathSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return builder.Length == 0 ? "track" : builder.ToString();
    }

    private sealed class MprisObject : IPathMethodHandler
    {
        private readonly MprisMediaTransportService _owner;

        public MprisObject(MprisMediaTransportService owner)
        {
            _owner = owner;
        }

        public string Path => ObjectPathString;

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            if (context.IsDBusIntrospectRequest)
            {
                context.ReplyIntrospectXml(IntrospectionXml);
                return ValueTask.CompletedTask;
            }

            var request = context.Request;
            var interfaceName = request.InterfaceAsString ?? string.Empty;
            var memberName = request.MemberAsString ?? string.Empty;

            if (interfaceName == PropertiesInterface)
            {
                HandleProperties(context, memberName);
                return ValueTask.CompletedTask;
            }

            if (interfaceName == RootInterface)
            {
                ReplyEmpty(context);
                return ValueTask.CompletedTask;
            }

            if (interfaceName == PlayerInterface)
            {
                HandlePlayer(context, memberName);
                return ValueTask.CompletedTask;
            }

            context.ReplyUnknownMethodError();
            return ValueTask.CompletedTask;
        }

        private void HandleProperties(MethodContext context, string memberName)
        {
            var reader = context.Request.GetBodyReader();

            switch (memberName)
            {
                case "Get":
                {
                    var interfaceName = reader.ReadString();
                    var propertyName = reader.ReadString();
                    var value = _owner.GetProperty(interfaceName, propertyName);
                    if (value is null)
                    {
                        context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", $"Unknown property '{propertyName}'.");
                        return;
                    }

                    using var writer = context.CreateReplyWriter("v");
                    writer.WriteVariant(value.Value);
                    context.Reply(writer.CreateMessage());
                    break;
                }
                case "GetAll":
                {
                    var interfaceName = reader.ReadString();
                    var properties = _owner.GetAllProperties(interfaceName);
                    using var writer = context.CreateReplyWriter("a{sv}");
                    writer.WriteDictionary(properties);
                    context.Reply(writer.CreateMessage());
                    break;
                }
                case "Set":
                    context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", "Properties are read-only.");
                    break;
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
        }

        private void HandlePlayer(MethodContext context, string memberName)
        {
            var reader = context.Request.GetBodyReader();

            switch (memberName)
            {
                case "Play":
                    _owner.RaisePlayRequested();
                    ReplyEmpty(context);
                    break;
                case "Pause":
                    _owner.RaisePauseRequested();
                    ReplyEmpty(context);
                    break;
                case "PlayPause":
                    if (_owner.GetPlaybackStatus() == "Playing")
                    {
                        _owner.RaisePauseRequested();
                    }
                    else
                    {
                        _owner.RaisePlayRequested();
                    }

                    ReplyEmpty(context);
                    break;
                case "Next":
                    _owner.RaiseNextRequested();
                    ReplyEmpty(context);
                    break;
                case "Previous":
                    _owner.RaisePreviousRequested();
                    ReplyEmpty(context);
                    break;
                case "Stop":
                    _owner.RaisePauseRequested();
                    ReplyEmpty(context);
                    break;
                case "Seek":
                    _owner.RaiseSeekRequested(_owner._position + FromMicroseconds(reader.ReadInt64()));
                    ReplyEmpty(context);
                    break;
                case "SetPosition":
                {
                    var trackId = reader.ReadString();
                    var requestedPosition = FromMicroseconds(reader.ReadInt64());
                    if (string.Equals(trackId, _owner.GetCurrentTrackObjectPath(), StringComparison.Ordinal))
                    {
                        _owner.RaiseSeekRequested(requestedPosition);
                    }

                    ReplyEmpty(context);
                    break;
                }
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
        }

        private static void ReplyEmpty(MethodContext context)
        {
            using var writer = context.CreateReplyWriter(null);
            context.Reply(writer.CreateMessage());
        }
    }
}
#else
#pragma warning disable CS0067
using AvaPlayer.Models;

namespace AvaPlayer.Services.MediaTransport;

public sealed class MprisMediaTransportService : IMediaTransportService
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
