namespace AvaPlayer.Services.Settings;

/// <summary>
/// Scoped service that provides the playback-position-memory preference
/// to both PlayerBarViewModel (read) and SettingsViewModel (read/write).
/// Eliminates the prior static SettingsPropertyMapper bridge.
/// </summary>
public interface IPlaybackPositionMemoryService
{
    bool IsEnabled { get; set; }
    Task LoadAsync(CancellationToken cancellationToken = default);
}
