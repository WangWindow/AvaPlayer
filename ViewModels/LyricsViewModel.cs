using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvaPlayer.Models;
using AvaPlayer.Services.Database;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.ViewModels;

public enum LyricFontPreset
{
    Small,
    Medium,
    Large
}

public partial class LyricLineViewModel : ObservableObject
{
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool IsNearCurrent { get; set; }

    [ObservableProperty]
    public partial double DisplayScale { get; set; } = 1.0;

    [ObservableProperty]
    public partial double DisplayOpacity { get; set; } = 0.34;

    public string Text { get; init; } = string.Empty;

    public TimeSpan Time { get; init; }
}

public partial class LyricsViewModel : ObservableObject
{
    private const string FontPresetSettingKey = "lyrics-font-preset";
    private const string AutoCenterSettingKey = "lyrics-auto-center";
    private const string ClickSeekSettingKey = "lyrics-click-seek";

    private readonly IDatabaseService _databaseService;
    private readonly ILogger<LyricsViewModel> _logger;
    private int _currentLineIndex = -1;
    private bool _isLoadingSettings;

    public LyricsViewModel(IDatabaseService databaseService, ILogger<LyricsViewModel> logger)
    {
        _databaseService = databaseService;
        _logger = logger;
    }

    public ObservableCollection<LyricLineViewModel> Lines { get; } = new();

    [ObservableProperty]
    public partial bool HasLyrics { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool ShowEmptyState { get; set; } = true;

    [ObservableProperty]
    public partial LyricFontPreset FontPreset { get; set; } = LyricFontPreset.Medium;

    [ObservableProperty]
    public partial bool IsAutoCenterEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool IsLyricClickSeekEnabled { get; set; } = true;

    public event EventHandler<int>? ScrollToLineRequested;
    public event EventHandler<TimeSpan>? SeekRequested;

    public bool IsSmallFontPreset => FontPreset == LyricFontPreset.Small;

    public bool IsMediumFontPreset => FontPreset == LyricFontPreset.Medium;

    public bool IsLargeFontPreset => FontPreset == LyricFontPreset.Large;

    public double InactiveLineFontSize => FontPreset switch
    {
        LyricFontPreset.Small => 14,
        LyricFontPreset.Large => 20,
        _ => 17
    };

    public double NearbyLineFontSize => FontPreset switch
    {
        LyricFontPreset.Small => 16,
        LyricFontPreset.Large => 22,
        _ => 19
    };

    public double ActiveLineFontSize => FontPreset switch
    {
        LyricFontPreset.Small => 20,
        LyricFontPreset.Large => 26,
        _ => 23
    };

    // Scale is relative to the base rendered font size (23 = Medium active line).
    private double BaseFontSize => 23;

    public double ActiveLineScale => ActiveLineFontSize / BaseFontSize;

    public double NearbyLineScale => FontPreset switch
    {
        LyricFontPreset.Small => 16d / BaseFontSize,
        LyricFontPreset.Large => 22d / BaseFontSize,
        _ => 19d / BaseFontSize
    };

    public double InactiveLineScale => FontPreset switch
    {
        LyricFontPreset.Small => 14d / BaseFontSize,
        LyricFontPreset.Large => 20d / BaseFontSize,
        _ => 17d / BaseFontSize
    };

    public double EstimatedLineHeight => FontPreset switch
    {
        LyricFontPreset.Small => 54,
        LyricFontPreset.Large => 66,
        _ => 60
    };

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _isLoadingSettings = true;
        try
        {
            var fontPreset = await _databaseService.GetSettingAsync(FontPresetSettingKey, cancellationToken);
            if (Enum.TryParse<LyricFontPreset>(fontPreset, ignoreCase: true, out var parsedPreset))
            {
                FontPreset = parsedPreset;
            }

            var autoCenter = await _databaseService.GetSettingAsync(AutoCenterSettingKey, cancellationToken);
            if (bool.TryParse(autoCenter, out var parsedAutoCenter))
            {
                IsAutoCenterEnabled = parsedAutoCenter;
            }

            var clickSeek = await _databaseService.GetSettingAsync(ClickSeekSettingKey, cancellationToken);
            if (bool.TryParse(clickSeek, out var parsedClickSeek))
            {
                IsLyricClickSeekEnabled = parsedClickSeek;
            }
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    public void BeginLoading()
    {
        Lines.Clear();
        _currentLineIndex = -1;
        HasLyrics = false;
        IsLoading = true;
        ShowEmptyState = false;
    }

    public void LoadLyrics(IReadOnlyList<LyricLine> lines)
    {
        Lines.Clear();
        _currentLineIndex = -1;

        foreach (var line in lines)
        {
            Lines.Add(new LyricLineViewModel
            {
                Text = line.Text,
                Time = line.Time
            });
        }

        HasLyrics = Lines.Count > 0;
        IsLoading = false;
        ShowEmptyState = !HasLyrics;
        RefreshLineVisuals();
    }

    public void ClearLyrics()
    {
        Lines.Clear();
        _currentLineIndex = -1;
        HasLyrics = false;
        IsLoading = false;
        ShowEmptyState = true;
    }

    public void UpdatePosition(double positionSeconds)
    {
        if (Lines.Count == 0)
        {
            return;
        }

        var playbackTime = TimeSpan.FromSeconds(positionSeconds);
        var newIndex = -1;

        for (var i = Lines.Count - 1; i >= 0; i--)
        {
            if (Lines[i].Time <= playbackTime)
            {
                newIndex = i;
                break;
            }
        }

        if (newIndex == _currentLineIndex)
        {
            return;
        }

        _currentLineIndex = newIndex;
        RefreshLineVisuals();

        if (newIndex >= 0 && newIndex < Lines.Count)
        {
            ScrollToLineRequested?.Invoke(this, newIndex);
        }
    }

    partial void OnFontPresetChanged(LyricFontPreset value)
    {
        OnPropertyChanged(nameof(IsSmallFontPreset));
        OnPropertyChanged(nameof(IsMediumFontPreset));
        OnPropertyChanged(nameof(IsLargeFontPreset));
        OnPropertyChanged(nameof(InactiveLineFontSize));
        OnPropertyChanged(nameof(NearbyLineFontSize));
        OnPropertyChanged(nameof(ActiveLineFontSize));
        OnPropertyChanged(nameof(InactiveLineScale));
        OnPropertyChanged(nameof(NearbyLineScale));
        OnPropertyChanged(nameof(ActiveLineScale));
        OnPropertyChanged(nameof(EstimatedLineHeight));
        RefreshLineVisuals();

        if (_currentLineIndex >= 0 && _currentLineIndex < Lines.Count)
        {
            ScrollToLineRequested?.Invoke(this, _currentLineIndex);
        }

        SaveSettingAsync(FontPresetSettingKey, value.ToString());
    }

    partial void OnIsAutoCenterEnabledChanged(bool value) =>
        SaveSettingAsync(AutoCenterSettingKey, value.ToString());

    partial void OnIsLyricClickSeekEnabledChanged(bool value) =>
        SaveSettingAsync(ClickSeekSettingKey, value.ToString());

    [RelayCommand]
    private void SeekToLine(LyricLineViewModel? line)
    {
        if (line is null || !IsLyricClickSeekEnabled)
        {
            return;
        }

        SeekRequested?.Invoke(this, line.Time);
    }

    [RelayCommand]
    private void UseSmallFont() => FontPreset = LyricFontPreset.Small;

    [RelayCommand]
    private void UseMediumFont() => FontPreset = LyricFontPreset.Medium;

    [RelayCommand]
    private void UseLargeFont() => FontPreset = LyricFontPreset.Large;

    [RelayCommand]
    private void ToggleAutoCenter() => IsAutoCenterEnabled = !IsAutoCenterEnabled;

    [RelayCommand]
    private void ToggleLyricClickSeek() => IsLyricClickSeekEnabled = !IsLyricClickSeekEnabled;

    private async void SaveSettingAsync(string key, string value)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        try
        {
            await _databaseService.SaveSettingAsync(key, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Lyrics] 保存设置失败: {Message}", ex.Message);
        }
    }

    private void RefreshLineVisuals()
    {
        if (Lines.Count == 0)
        {
            return;
        }

        if (_currentLineIndex < 0 || _currentLineIndex >= Lines.Count)
        {
            foreach (var line in Lines)
            {
                line.IsCurrent = false;
                line.IsNearCurrent = false;
                line.DisplayScale = InactiveLineScale;
                line.DisplayOpacity = 0.34;
            }

            return;
        }

        for (var i = 0; i < Lines.Count; i++)
        {
            var distance = Math.Abs(i - _currentLineIndex);
            var line = Lines[i];

            line.IsCurrent = distance == 0;
            line.IsNearCurrent = distance is 1 or 2;
            line.DisplayScale = distance switch
            {
                0 => ActiveLineScale,
                1 => NearbyLineScale,
                _ => InactiveLineScale
            };

            line.DisplayOpacity = distance switch
            {
                0 => 1,
                1 => 0.7,
                2 => 0.48,
                _ => 0.26
            };
        }
    }
}
