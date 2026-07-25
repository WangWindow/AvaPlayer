using AvaPlayer.Services.Settings;

namespace AvaPlayer.Application.Tests.Fakes;

/// <summary>
/// Hand-written fake of <see cref="ISettingsService"/> for unit testing.
/// Stores key/value pairs in an in-memory dictionary.
/// </summary>
public sealed class FakeSettingsService : ISettingsService
{
    private readonly Dictionary<string, string> _store = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns all currently stored key/value pairs for test assertions.
    /// </summary>
    public IReadOnlyDictionary<string, string> AllValues =>
        new Dictionary<string, string>(_store);

    /// <summary>
    /// Number of times <see cref="SaveSettingsBatchAsync"/> was called.
    /// </summary>
    public int BatchCallCount { get; private set; }

    /// <summary>
    /// All settings dictionaries passed to <see cref="SaveSettingsBatchAsync"/>.
    /// </summary>
    public List<IReadOnlyDictionary<string, string>> BatchCalls { get; } = new();

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_store.TryGetValue(key, out var value))
            return Task.FromResult<string?>(value);

        return Task.FromResult<string?>(null);
    }

    public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        _store[key] = value;
        return Task.CompletedTask;
    }

    public Task SaveSettingsBatchAsync(IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken = default)
    {
        BatchCallCount++;
        BatchCalls.Add(new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase));
        foreach (var (key, value) in settings)
            _store[key] = value;
        return Task.CompletedTask;
    }
}
