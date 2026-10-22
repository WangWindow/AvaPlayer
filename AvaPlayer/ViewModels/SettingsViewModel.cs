using System.ComponentModel;
using System.Globalization;
using Avalonia;
using AvaPlayer.Services.Cache;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvaPlayer.Services.Settings;
using AvaPlayer.Services.Network;

namespace AvaPlayer.ViewModels;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private const string TrayIconStyleSettingKey = "tray-icon-style";

    private readonly ISettingsService _settings;
    private readonly ICacheService _cache;
    private readonly INetworkAccessService _networkAccessService;
    private readonly ILyricPreferencesService _lyricPreferences;
    private readonly IPlaybackPositionMemoryService _positionMemory;
    private bool _isLoadingSettings;
    private bool _disposed;

    public SettingsViewModel(
        ISettingsService settings,
        INetworkAccessService networkAccessService,
        ILyricPreferencesService lyricPreferences,
        IPlaybackPositionMemoryService positionMemory,
        ICacheService cache)
    {
        _settings = settings;
        _cache = cache;
        _networkAccessService = networkAccessService;
        _lyricPreferences = lyricPreferences;
        _positionMemory = positionMemory;
        _lyricPreferences.PropertyChanged += OnLyricPreferenceChanged;
    }

    /// <summary>
    /// Exposes the shared lyric preference state for XAML bindings.
    /// SettingsView binds font/auto-center/click-seek through this property.
    /// </summary>
    public ILyricPreferencesService LyricsPreferences => _lyricPreferences;

    // ── Lyric font preset indicators (sourced from shared preferences) ──

    public bool IsSmallFontPreset => _lyricPreferences.FontPreset == LyricFontPreset.Small;

    public bool IsMediumFontPreset => _lyricPreferences.FontPreset == LyricFontPreset.Medium;

    public bool IsLargeFontPreset => _lyricPreferences.FontPreset == LyricFontPreset.Large;

    // ── Synced from PlayerBarViewModel via MainWindowViewModel ──

    [ObservableProperty]
    public partial bool IsSettingsVisible { get; set; }

    [ObservableProperty]
    public partial bool UseRoundedWindowCorners { get; set; } = true;

    partial void OnUseRoundedWindowCornersChanged(bool value)
    {
        if (!_isLoadingSettings) _ = _settings.SetAsync("rounded-window-corners", value.ToString());
    }

    [ObservableProperty]
    public partial string CacheStatus { get; set; } = string.Empty;

    public event EventHandler? CacheRefreshing;
    public event EventHandler? CacheRefreshed;

    [RelayCommand]
    private async Task RefreshCacheAsync(CancellationToken cancellationToken)
    {
        CacheStatus = "正在刷新…";
        CacheRefreshing?.Invoke(this, EventArgs.Empty);
        try
        {
            await _cache.ClearCategoryAsync("lyrics", cancellationToken);
            await _cache.ClearCategoryAsync("album-art", cancellationToken);
            CacheStatus = "缓存已清除，已重新读取当前歌曲";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CacheStatus = $"刷新失败：{ex.Message}";
        }
        finally { CacheRefreshed?.Invoke(this, EventArgs.Empty); }
    }

    // ── Network ──

    [ObservableProperty]
    public partial bool IsNetworkEnabled { get; set; } = true;

    partial void OnIsNetworkEnabledChanged(bool value)
    {
        if (_isLoadingSettings) return;

        _networkAccessService.IsNetworkEnabled = value;
        _ = _networkAccessService.PersistAsync();
    }

    // ── Tray Icon ──

    [ObservableProperty]
    public partial string TrayIconStyle { get; set; } = "dark";

    public bool IsTrayIconLight => TrayIconStyle == "light";
    public bool IsTrayIconDark => TrayIconStyle == "dark";

    [RelayCommand]
    private void UseLightTrayIcon() => TrayIconStyle = "light";

    [RelayCommand]
    private void UseDarkTrayIcon() => TrayIconStyle = "dark";

    partial void OnTrayIconStyleChanged(string value)
    {
        OnPropertyChanged(nameof(IsTrayIconLight));
        OnPropertyChanged(nameof(IsTrayIconDark));
        if (!_isLoadingSettings) _ = _settings.SetAsync(TrayIconStyleSettingKey, value);
    }

    // ── Playback Position Memory ──

    /// <summary>
    /// Gets or sets whether playback position memory is enabled.
    /// Persisted to DB and synchronized to the shared IPlaybackPositionMemoryService
    /// so that PlayerBarViewModel reads the same value from its own injection.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPlaybackPositionMemoryEnabled { get; set; } = true;

    partial void OnIsPlaybackPositionMemoryEnabledChanged(bool value)
    {
        if (_disposed) return;
        _positionMemory.IsEnabled = value;
    }

    // ── Lyric preferences commands (delegate to shared state) ──

    [RelayCommand]
    private void UseSmallFont() => _lyricPreferences.FontPreset = LyricFontPreset.Small;

    [RelayCommand]
    private void UseMediumFont() => _lyricPreferences.FontPreset = LyricFontPreset.Medium;

    [RelayCommand]
    private void UseLargeFont() => _lyricPreferences.FontPreset = LyricFontPreset.Large;

    [RelayCommand]
    private void ToggleAutoCenter() => _lyricPreferences.IsAutoCenterEnabled = !_lyricPreferences.IsAutoCenterEnabled;

    [RelayCommand]
    private void ToggleLyricClickSeek() => _lyricPreferences.IsLyricClickSeekEnabled = !_lyricPreferences.IsLyricClickSeekEnabled;

    // ── Preference change propagation ──

    private void OnLyricPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ILyricPreferencesService.FontPreset))
        {
            OnPropertyChanged(nameof(IsSmallFontPreset));
            OnPropertyChanged(nameof(IsMediumFontPreset));
            OnPropertyChanged(nameof(IsLargeFontPreset));
        }
    }

    // ── Settings loading ──

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _isLoadingSettings = true;
        try
        {
            IsNetworkEnabled = _networkAccessService.IsEnabled;
            var rounded = await _settings.GetAsync("rounded-window-corners", cancellationToken);
            UseRoundedWindowCorners = !bool.TryParse(rounded, out var enabled) || enabled;

            var savedStyle = await _settings.GetAsync(TrayIconStyleSettingKey, cancellationToken);
            if (savedStyle is "light" or "dark")
                TrayIconStyle = savedStyle;

            await _positionMemory.LoadAsync(cancellationToken);
            IsPlaybackPositionMemoryEnabled = _positionMemory.IsEnabled;

            await _lyricPreferences.LoadAsync(cancellationToken);
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    public async Task<Size> LoadWindowSizeAsync()
    {
        try
        {
            var width = await _settings.GetAsync("window-width");
            var height = await _settings.GetAsync("window-height");
            if (double.TryParse(width, CultureInfo.InvariantCulture, out var w) && double.IsFinite(w) && w > 0
                && double.TryParse(height, CultureInfo.InvariantCulture, out var h) && double.IsFinite(h) && h > 0)
                return new Size(w, h);
        }
        catch (Exception) { CacheStatus = "无法读取窗口大小"; }
        return new Size(1040, 760);
    }

    public async Task SaveWindowSizeAsync(Size size)
    {
        try
        {
            await _settings.SaveSettingsBatchAsync(new Dictionary<string, string>
            {
                ["window-width"] = size.Width.ToString(CultureInfo.InvariantCulture),
                ["window-height"] = size.Height.ToString(CultureInfo.InvariantCulture)
            });
        }
        catch (Exception) { CacheStatus = "无法保存窗口大小"; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lyricPreferences.PropertyChanged -= OnLyricPreferenceChanged;
    }
}
