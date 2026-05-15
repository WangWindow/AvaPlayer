namespace AvaPlayer.Services.Network;

public interface INetworkAccessService
{
    bool IsNetworkEnabled { get; set; }
    bool IsEnabled { get; }
    event EventHandler<bool>? NetworkAccessChanged;
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task PersistAsync(CancellationToken cancellationToken = default);
}
