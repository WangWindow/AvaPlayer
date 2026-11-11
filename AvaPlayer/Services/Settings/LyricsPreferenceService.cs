using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.Services.Settings;

public partial class LyricsPreferenceService(
    ISettingsService settings,
    ILogger<LyricsPreferenceService> logger) : ObservableObject, ILyricPreferencesService
{
    private const string FontPresetSettingKey = "lyrics-font-preset";
    private const string AutoCenterSettingKey = "lyrics-auto-center";
    private const string ClickSeekSettingKey = "lyrics-click-seek";

    private bool _isLoading;

    [ObservableProperty]
    public partial LyricFontPreset FontPreset { get; set; } = LyricFontPreset.Medium;

    [ObservableProperty]
    public partial bool IsAutoCenterEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsLyricClickSeekEnabled { get; set; } = true;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _isLoading = true;
        try
        {
            var fontPreset = await settings.GetAsync(FontPresetSettingKey, cancellationToken);
            if (Enum.TryParse<LyricFontPreset>(fontPreset, ignoreCase: true, out var parsedPreset))
            {
                FontPreset = parsedPreset;
            }

            var autoCenter = await settings.GetAsync(AutoCenterSettingKey, cancellationToken);
            if (bool.TryParse(autoCenter, out var parsedAutoCenter))
            {
                IsAutoCenterEnabled = parsedAutoCenter;
            }

            var clickSeek = await settings.GetAsync(ClickSeekSettingKey, cancellationToken);
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
            await settings.SetAsync(key, value);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[LyricsPreferences] 保存设置失败: {Key}={Value}", key, value);
        }
    }
}
