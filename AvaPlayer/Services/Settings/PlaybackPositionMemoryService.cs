using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.Settings;

public sealed class PlaybackPositionMemoryService : IPlaybackPositionMemoryService
{
    private const string SettingKey = "playback-position-memory";
    private readonly ISettingsService _settings;
    private readonly ILogger<PlaybackPositionMemoryService> _logger;
    private bool _isLoading;

    public PlaybackPositionMemoryService(ISettingsService settings, ILogger<PlaybackPositionMemoryService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            if (!_isLoading)
            {
                PersistSetting(value.ToString());
            }
        }
    }
    private bool _isEnabled = true;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _isLoading = true;
        try
        {
            var saved = await _settings.GetAsync(SettingKey, cancellationToken);
            if (bool.TryParse(saved, out var isEnabled))
            {
                _isEnabled = isEnabled;
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void PersistSetting(string value)
    {
        _ = PersistSettingAsync(value);
    }

    private async Task PersistSettingAsync(string value)
    {
        try
        {
            await _settings.SetAsync(SettingKey, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PlaybackPositionMemory] 保存设置失败: {Value}", value);
        }
    }
}
