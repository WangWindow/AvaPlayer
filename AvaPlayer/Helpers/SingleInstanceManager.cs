using System.IO.Pipes;
using System.Threading;

namespace AvaPlayer.Helpers;

internal sealed class SingleInstanceManager : IDisposable
{
    private const int ActivationConnectTimeoutMilliseconds = 1500;

    private readonly FileStream? _lockStream;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _listenerCancellation = new();
    private Task? _listenerTask;
    private int _pendingActivationRequests;
    private bool _disposed;

    private SingleInstanceManager(FileStream? lockStream, string pipeName)
    {
        _lockStream = lockStream;
        _pipeName = pipeName;
    }

    public bool IsPrimaryInstance => _lockStream is not null;

    public event EventHandler? ActivationRequested;

    public static SingleInstanceManager Create(string appId)
    {
        var lockDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appId);

        Directory.CreateDirectory(lockDirectory);

        var lockPath = Path.Combine(lockDirectory, ".instance.lock");
        var pipeName = NormalizePipeName(appId);

        try
        {
            var lockStream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstanceManager(lockStream, pipeName);
        }
        catch (IOException)
        {
            return new SingleInstanceManager(lockStream: null, pipeName);
        }
    }

    public void StartListening()
    {
        if (!IsPrimaryInstance || _listenerTask is not null)
        {
            return;
        }

        _listenerTask = Task.Run(ListenForActivationRequestsAsync);
    }

    public int ConsumePendingActivationRequests() => Interlocked.Exchange(ref _pendingActivationRequests, 0);

    public bool TrySignalPrimaryInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(ActivationConnectTimeoutMilliseconds);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            Console.Error.WriteLine($"[SingleInstance] 通知现有实例失败: {ex.Message}");
            return false;
        }
    }

    private async Task ListenForActivationRequestsAsync()
    {
        while (!_listenerCancellation.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;

            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_listenerCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                server?.Dispose();

                if (!_listenerCancellation.IsCancellationRequested)
                {
                    Console.Error.WriteLine($"[SingleInstance] 监听激活请求失败: {ex.Message}");
                }

                continue;
            }

            try
            {
                var handler = ActivationRequested;
                if (handler is not null)
                {
                    handler(this, EventArgs.Empty);
                }
                else
                {
                    Interlocked.Increment(ref _pendingActivationRequests);
                }
            }
            finally
            {
                server.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listenerCancellation.Cancel();
        _lockStream?.Dispose();
        _listenerCancellation.Dispose();
    }

    private static string NormalizePipeName(string appId)
    {
        var buffer = new char[appId.Length + 12];
        var index = 0;

        foreach (var character in appId)
        {
            buffer[index++] = char.IsLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : '_';
        }

        "_activation".AsSpan().CopyTo(buffer.AsSpan(index));
        return new string(buffer, 0, index + "_activation".Length);
    }
}
