using AvaPlayer.Services.Database;

namespace AvaPlayer.Services.Settings;

public sealed class AppSettingsService : ISettingsService
{
    private readonly IDatabaseService _database;

    public AppSettingsService(IDatabaseService database)
    {
        _database = database;
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        _database.GetSettingAsync(key, cancellationToken);

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default) =>
        _database.SaveSettingAsync(key, value, cancellationToken);

    public Task SaveSettingsBatchAsync(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default) =>
        _database.SaveSettingsBatchAsync(settings, cancellationToken);
}
