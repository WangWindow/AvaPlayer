using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AvaPlayer.Services.Lyrics;
using AvaPlayer.Services.Settings;
using Microsoft.Extensions.Logging;

namespace AvaPlayer.ViewModels;

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

public partial class LyricsViewModel : ObservableObject, ILyricPresentationService, IDisposable
{
    private readonly ILyricPreferencesService _preferences;
    private readonly ILogger<LyricsViewModel> _logger;
    private int _currentLineIndex = -1;
    private bool _disposed;

    public LyricsViewModel(ILyricPreferencesService preferences, ILogger<LyricsViewModel> logger)
    {
        _preferences = preferences;
        _logger = logger;
        _preferences.PropertyChanged += OnPreferenceChanged;
    }

    public ObservableCollection<LyricLineViewModel> Lines { get; } = new();

    [ObservableProperty]
    private bool _hasLyrics;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _showEmptyState = true;

    public event EventHandler<int>? ScrollToLineRequested;
    public event EventHandler<TimeSpan>? SeekRequested;

    // ── Font preset accessors derived from shared preferences ──

    public LyricFontPreset FontPreset => _preferences.FontPreset;

    public bool IsAutoCenterEnabled => _preferences.IsAutoCenterEnabled;

    public bool IsLyricClickSeekEnabled => _preferences.IsLyricClickSeekEnabled;

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

    // ── ILyricPresentationService implementation ──

    void ILyricPresentationService.BeginLoading() => BeginLoading();

    void ILyricPresentationService.LoadLyrics(IReadOnlyList<AvaPlayer.Models.LyricLine> lines) => LoadLyrics(lines);

    void ILyricPresentationService.ClearLyrics() => ClearLyrics();

    void ILyricPresentationService.UpdatePosition(double positionSeconds) => UpdatePosition(positionSeconds);

    // ── Public API ──

    public void BeginLoading()
    {
        Lines.Clear();
        _currentLineIndex = -1;
        HasLyrics = false;
        IsLoading = true;
        ShowEmptyState = false;
    }

    public void LoadLyrics(IReadOnlyList<AvaPlayer.Models.LyricLine> lines)
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

    // ── Preference change propagation ──

    private void OnPreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ILyricPreferencesService.FontPreset):
                OnPropertyChanged(nameof(FontPreset));
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
                break;

            case nameof(ILyricPreferencesService.IsAutoCenterEnabled):
                OnPropertyChanged(nameof(IsAutoCenterEnabled));
                break;

            case nameof(ILyricPreferencesService.IsLyricClickSeekEnabled):
                OnPropertyChanged(nameof(IsLyricClickSeekEnabled));
                break;
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

    // ── Commands (operate on shared preferences) ──

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
    private void UseSmallFont() => _preferences.FontPreset = LyricFontPreset.Small;

    [RelayCommand]
    private void UseMediumFont() => _preferences.FontPreset = LyricFontPreset.Medium;

    [RelayCommand]
    private void UseLargeFont() => _preferences.FontPreset = LyricFontPreset.Large;

    [RelayCommand]
    private void ToggleAutoCenter() => _preferences.IsAutoCenterEnabled = !_preferences.IsAutoCenterEnabled;

    [RelayCommand]
    private void ToggleLyricClickSeek() => _preferences.IsLyricClickSeekEnabled = !_preferences.IsLyricClickSeekEnabled;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _preferences.PropertyChanged -= OnPreferenceChanged;
    }
}
