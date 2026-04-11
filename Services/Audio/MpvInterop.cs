using System.Runtime.InteropServices;

namespace AvaPlayer.Services.Audio;

internal static partial class MpvInterop
{
    public const string LibraryName = "libmpv-2";

    [DllImport(LibraryName, EntryPoint = "mpv_create")]
    internal static extern IntPtr Create();

    [DllImport(LibraryName, EntryPoint = "mpv_initialize")]
    internal static extern int Initialize(IntPtr handle);

    [DllImport(LibraryName, EntryPoint = "mpv_terminate_destroy")]
    internal static extern void TerminateDestroy(IntPtr handle);

    [DllImport(LibraryName, EntryPoint = "mpv_set_option_string")]
    internal static extern int SetOptionString(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibraryName, EntryPoint = "mpv_set_property_string")]
    internal static extern int SetPropertyString(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(LibraryName, EntryPoint = "mpv_observe_property")]
    internal static extern int ObserveProperty(
        IntPtr handle,
        ulong replyUserData,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        MpvFormat format);

    [DllImport(LibraryName, EntryPoint = "mpv_wait_event")]
    internal static extern IntPtr WaitEvent(IntPtr handle, double timeout);

    [DllImport(LibraryName, EntryPoint = "mpv_error_string")]
    internal static extern IntPtr ErrorString(int error);

    [DllImport(LibraryName, EntryPoint = "mpv_command")]
    internal static unsafe extern int Command(IntPtr handle, byte** arguments);
}

internal enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 7,
    NodeArray = 8,
    NodeMap = 9,
    ByteArray = 10
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    Idle = 11,
    Tick = 14,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25
}

internal enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct MpvEvent
{
    public MpvEventId EventId { get; init; }
    public int Error { get; init; }
    public ulong ReplyUserData { get; init; }
    public IntPtr Data { get; init; }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct MpvEventProperty
{
    public IntPtr Name { get; init; }
    public MpvFormat Format { get; init; }
    public IntPtr Data { get; init; }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct MpvEventEndFile
{
    public MpvEndFileReason Reason { get; init; }
    public int Error { get; init; }
    public int PlaylistEntryId { get; init; }
    public int PlaylistInsertId { get; init; }
    public int PlaylistInsertNumEntries { get; init; }
    public int PlaylistInsertPosition { get; init; }
}
