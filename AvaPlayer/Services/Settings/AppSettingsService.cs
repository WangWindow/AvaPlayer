using AvaPlayer.Services.Database;

namespace AvaPlayer.Services.Settings;

public sealed class AppSettingsService(IDatabaseService database) : ISettingsService
{
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        database.GetSettingAsync(key, cancellationToken);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
        database.SaveSettingAsync(key, value, cancellationToken);

    public Task SaveSettingsBatchAsync(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default) =>
        database.SaveSettingsBatchAsync(settings, cancellationToken);
}
