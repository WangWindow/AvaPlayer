using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.Settings;

public sealed class PlaybackPositionMemoryService(
    ISettingsService settings,
    ILogger<PlaybackPositionMemoryService> logger) : IPlaybackPositionMemoryService
{
    private const string SettingKey = "playback-position-memory";
    private bool _isLoading;

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
            var saved = await settings.GetAsync(SettingKey, cancellationToken);
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
            await settings.SetAsync(SettingKey, value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[PlaybackPositionMemory] 保存设置失败: {Value}", value);
        }
    }
}
