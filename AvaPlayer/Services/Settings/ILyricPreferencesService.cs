using System.ComponentModel;

namespace AvaPlayer.Services.Settings;

public enum LyricFontPreset
{
    Small,
    Medium,
    Large
}

/// <summary>
/// Shared scoped state for lyric font, auto-center, and click-seek preferences.
/// Single persistence owner; both SettingsViewModel and LyricsViewModel observe
/// this instance for immediate UI updates without cross-VM references.
/// </summary>
public interface ILyricPreferencesService : INotifyPropertyChanged
{
    LyricFontPreset FontPreset { get; set; }
    bool IsAutoCenterEnabled { get; set; }
    bool IsLyricClickSeekEnabled { get; set; }
    Task LoadAsync(CancellationToken cancellationToken = default);
}
