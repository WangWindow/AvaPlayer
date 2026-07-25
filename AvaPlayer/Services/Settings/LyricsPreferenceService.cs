using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.Settings;

public partial class LyricsPreferenceService : ObservableObject, ILyricPreferencesService
{
    private const string FontPresetSettingKey = "lyrics-font-preset";
    private const string AutoCenterSettingKey = "lyrics-auto-center";
    private const string ClickSeekSettingKey = "lyrics-click-seek";

    private readonly ISettingsService _settings;
    private readonly ILogger<LyricsPreferenceService> _logger;
    private bool _isLoading;

    public LyricsPreferenceService(ISettingsService settings, ILogger<LyricsPreferenceService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    [ObservableProperty]
    private LyricFontPreset _fontPreset = LyricFontPreset.Medium;

    [ObservableProperty]
    private bool _isAutoCenterEnabled = true;

    [ObservableProperty]
    private bool _isLyricClickSeekEnabled = true;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _isLoading = true;
        try
        {
            var fontPreset = await _settings.GetAsync(FontPresetSettingKey, cancellationToken);
            if (Enum.TryParse<LyricFontPreset>(fontPreset, ignoreCase: true, out var parsedPreset))
            {
                FontPreset = parsedPreset;
            }

            var autoCenter = await _settings.GetAsync(AutoCenterSettingKey, cancellationToken);
            if (bool.TryParse(autoCenter, out var parsedAutoCenter))
            {
                IsAutoCenterEnabled = parsedAutoCenter;
            }

            var clickSeek = await _settings.GetAsync(ClickSeekSettingKey, cancellationToken);
            if (bool.TryParse(clickSeek, out var parsedClickSeek))
            {
                IsLyricClickSeekEnabled = parsedClickSeek;
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    partial void OnFontPresetChanged(LyricFontPreset value)
    {
        if (!_isLoading)
        {
            PersistSetting(FontPresetSettingKey, value.ToString());
        }
    }

    partial void OnIsAutoCenterEnabledChanged(bool value)
    {
        if (!_isLoading)
        {
            PersistSetting(AutoCenterSettingKey, value.ToString());
        }
    }

    partial void OnIsLyricClickSeekEnabledChanged(bool value)
    {
        if (!_isLoading)
        {
            PersistSetting(ClickSeekSettingKey, value.ToString());
        }
    }

    private void PersistSetting(string key, string value)
    {
        _ = PersistSettingAsync(key, value);
    }

    private async Task PersistSettingAsync(string key, string value)
    {
        try
        {
            await _settings.SetAsync(key, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LyricsPreferences] 保存设置失败: {Key}={Value}", key, value);
        }
    }
}
