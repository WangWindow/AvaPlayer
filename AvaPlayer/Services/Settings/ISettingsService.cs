namespace AvaPlayer.Services.Settings;

public interface ISettingsService
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
    /// <summary>
    /// Saves multiple settings atomically within a single transaction.
    /// </summary>
    Task SaveSettingsBatchAsync(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default);
}
