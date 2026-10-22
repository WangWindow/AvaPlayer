using CommunityToolkit.Mvvm.ComponentModel;

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

    public bool IsTimed { get; init; } = true;

    public double DisplayFontSize => 23 * DisplayScale;

    partial void OnDisplayScaleChanged(double value) => OnPropertyChanged(nameof(DisplayFontSize));

    public string Text { get; init; } = string.Empty;

    public TimeSpan Time { get; init; }
}
